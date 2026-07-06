using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel.Listener;

namespace ReverseTunnel.Yarp.Tunnel
{

    public class TunnelClientManager : ITunnelClientManager, IDisposable
    {
        private readonly ILogger<TunnelClientManager> _logger;
        private readonly ITunnelClientOptionsStore _optionsStore;
        private readonly ITunnelClientFactory _tunnelClientFactory;
        private readonly IOptions<TunnelListenerOptions> _listenerOptions;
        public string LastConnectErrorMessage { get; private set; } = string.Empty;
        public int? LastConnectStatusCode { get; private set; } = null;
        public string LastErrorResponseBody { get; private set; } = string.Empty;
        private TunnelConnectionState _state;
        private TunnelClient? _client;
        // Combined atomic flag for pending options changes.
        // Bit 0 (OptionsChangedBit)  – any options change is pending.
        // Bit 1 (EndpointChangedBit) – the change affects the connection endpoint/shape.
        // Written from the event-handler thread via CAS; read+reset atomically in the loop.
        private int _pendingOptionsChange = 0;
        private const int OptionsChangedBit = 1;
        private const int EndpointChangedBit = 2;
        private bool _stepdownErrorLogging = false;
        private readonly WorkerWithBackoff _reconnectWorker;
        public event EventHandler<TunnelConnectionState>? ConnectionStateChanged;

        public TunnelClient? Tunnel => _client;

        /// <summary>Exposed for test access via InternalsVisibleTo only.</summary>
        internal TunnelConnectionState State
        {
            get => _state;
            set => _state = value;
        }

        /// <summary>Exposed for test access via InternalsVisibleTo only.</summary>
        internal TunnelClient? ActiveClient
        {
            get => Volatile.Read(ref _client);
            set => Volatile.Write(ref _client, value);
        }

        public TunnelClientManager(ITunnelClientOptionsStore optionsStore, IOptions<TunnelListenerOptions> listenerOptions, ITunnelClientFactory tunnelClientFactory, ILogger<TunnelClientManager> logger)
        {
            _optionsStore = optionsStore;
            _listenerOptions = listenerOptions;
            _tunnelClientFactory = tunnelClientFactory;
            _logger = logger;

            _state = TunnelConnectionState.Disconnected;
            _reconnectWorker = new(listenerOptions.Value.ReconnectInterval, listenerOptions.Value.MaxReconnectInterval, ReconnectLoopAsync);

            _optionsStore.OptionsChanged += (_, args) =>
            {
                // Track whether the change requires tearing down an existing connection
                // (endpoint, identity, or connection-shaping settings changed),
                // vs just updating credentials (e.g. JWT refresh).
                bool isEndpointChange =
                    args.OldOptions.TunnelHost != args.NewOptions.TunnelHost ||
                    args.OldOptions.TunnelId != args.NewOptions.TunnelId ||
                    args.OldOptions.IsEnabled != args.NewOptions.IsEnabled ||
                    args.OldOptions.TunnelPathTemplate != args.NewOptions.TunnelPathTemplate ||
                    !Equals(args.OldOptions.WebSocketOptions, args.NewOptions.WebSocketOptions);

                if (!isEndpointChange)
                {
                    // Credentials-only change (e.g. a JWT refresh). The connection does not need to
                    // be touched: an established tunnel stays up and picks up the new credentials on
                    // its next natural reconnect, and while disconnected the reconnect worker already
                    // reads the current headers from the options store on every attempt. Resetting the
                    // worker here would only restart the reconnect backoff and, on a repeated refresh,
                    // race a fresh connection attempt against the in-flight one - so we deliberately
                    // do nothing.
                    _logger.LogDebug("Tunnel credentials changed; existing reconnect schedule and connection are kept.");
                    return;
                }

                // OR the new bits into the pending flag atomically so that:
                // - an endpoint change is never downgraded to credentials-only by a
                //   concurrent credentials-only update, and
                // - an update that arrives between the loop's read and reset is not lost.
                int addedFlags = OptionsChangedBit | EndpointChangedBit;
                int observed;
                do
                {
                    observed = Volatile.Read(ref _pendingOptionsChange);
                } while (Interlocked.CompareExchange(ref _pendingOptionsChange, observed | addedFlags, observed) != observed);

                _logger.LogInformation("Tunnel options changed (endpoint changed: {EndpointChanged}), triggering reconnect evaluation...", isEndpointChange);
                TriggerReconnect();
            };
        }

        public void Dispose()
        {
            _reconnectWorker.Dispose();
        }

        private void TriggerReconnect()
        {
            _reconnectWorker.Reset();
        }

        public TunnelConnectionState ConnectionState => _state;

        public bool IsEnabled => _optionsStore.Current.IsEnabled;

        private async Task<bool> ReconnectLoopAsync(CancellationToken token)
        {
            bool doBackoff = _listenerOptions.Value.EnableReconnectBackoff;
            // Atomically consume both flags so no concurrent update is lost.
            int flags = Interlocked.Exchange(ref _pendingOptionsChange, 0);
            if ((flags & OptionsChangedBit) != 0)
            {
                bool endpointChanged = (flags & EndpointChangedBit) != 0;

                if (_optionsStore.Current.IsEnabled)
                {
                    if (_state == TunnelConnectionState.Connected && !endpointChanged)
                    {
                        // Only credentials (e.g. JWT) were updated while already connected.
                        // No need to tear down the working connection - updated headers
                        // will be used automatically on the next reconnect after a genuine disconnect.
                        _logger.LogDebug("Options updated (credentials refresh) while already connected - keeping existing connection");
                        doBackoff = false;
                    }
                    else
                    {
                        await ConnectInternalAsync(token);
                    }
                }
                else
                {
                    await SetTunnelAsync(null);
                    UpdateState(TunnelConnectionState.Disconnected);
                }
            }
            else if (!_optionsStore.Current.IsEnabled)
            {
                // if the tunnel is disabled, we should not attempt to reconnect
                if (_state != TunnelConnectionState.Disconnected)
                {
                    await SetTunnelAsync(null);
                    UpdateState(TunnelConnectionState.Disconnected);
                }
            }
            else if (_state == TunnelConnectionState.Connected || _state == TunnelConnectionState.Connecting)
            {
                // we are already connected or connecting, do nothing
                doBackoff = false;
            }
            else
            {
                await ConnectInternalAsync(token);
            }

            return doBackoff;
        }

        private async Task ConnectInternalAsync(CancellationToken cancellationToken)
        {
            try
            {

                UpdateState(TunnelConnectionState.Connecting);

                var websocket = await _tunnelClientFactory.CreateAsync();
                if (websocket == null)
                {
                    _logger.LogWarning("WebSocket creation failed (TunnelClientFactory returned null).");
                    await SetTunnelAsync(null);
                    UpdateState(TunnelConnectionState.Error);
                }
                else
                {
                    // The websocket is owned by this method until it is handed to a TunnelClient.
                    // On every other exit (connect failure, cancellation, handshake error) the finally
                    // block disposes it, so a connected-but-unowned socket can never leak.
                    bool tunnelOwnsSocket = false;
                    try
                    {
                        var uri = await _tunnelClientFactory.GetUriAsync();
                        bool connected = false;
                        UpdateState(TunnelConnectionState.Connecting);

                        try
                        {
                            await websocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                            connected = true;
                            _stepdownErrorLogging = false;
                        }
                        catch (TaskCanceledException)
                        {
                            LastConnectErrorMessage = "Connection timed out";
                            LastConnectStatusCode = 0;
                            UpdateState(TunnelConnectionState.Disconnected);
                        }
                        catch (WebSocketException ex) when (ex.InnerException is HttpRequestException httpRequestException)
                        {
                            LastConnectErrorMessage = httpRequestException.Message;
                            LastConnectStatusCode = (int?)websocket.HttpStatusCode;
                            await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri, cancellationToken);
                            if (!_stepdownErrorLogging)
                            {
                                _logger.LogInformation(ex, "Failed to connect to {Uri}, {Message}: {HttpRequestErrorMessage} (Code: {StatusCode})", uri, ex.Message, httpRequestException.Message, httpRequestException.StatusCode);
                                _stepdownErrorLogging = true;
                            }
                            else
                            {
                                _logger.LogTrace("Failed to connect to {Uri}, {Message}: {HttpRequestErrorMessage} (Code: {StatusCode})", uri, ex.Message, httpRequestException.Message, httpRequestException.StatusCode);
                            }

                            UpdateState(TunnelConnectionState.Disconnected);
                        }
                        catch (WebSocketException ex)
                        {
                            LastConnectErrorMessage = ex.Message;
                            LastConnectStatusCode = (int?)websocket.HttpStatusCode;
                            await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri, cancellationToken);
                            _logger.LogDebug(ex, "Websocket Error: {Uri}, {Message}", uri, ex.Message);
                            UpdateState(TunnelConnectionState.Error);
                        }

                        if (connected)
                        {
                            LastErrorResponseBody = string.Empty;
                            LastConnectErrorMessage = string.Empty;
                            LastConnectStatusCode = (int?)websocket.HttpStatusCode;
                            _logger.LogInformation("Connected to {Uri}", uri);
                            var stream = await MultiplexingStream.CreateAsync(websocket.AsStream(), new MultiplexingStream.Options
                            {
                                ProtocolMajorVersion = 3
                            }, cancellationToken);

                            var tunnel = new TunnelClient(websocket, stream) { Uri = uri };

                            // Set _client BEFORE attaching the completion continuation.
                            // If Completion fires synchronously or very quickly, the guard
                            // comparison (Volatile.Read(ref _client) == tunnelRef) would
                            // otherwise see null and incorrectly skip the Disconnected transition.
                            await SetTunnelAsync(tunnel);
                            tunnelOwnsSocket = true;

                            var tunnelRef = tunnel;
                            _ = tunnel.Completion.ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                {
                                    _logger.LogDebug(t.Exception, "Tunnel stream ended with error");
                                }
                                // Only set Disconnected if this tunnel is still the active one.
                                // When a new connection replaces this one (e.g. during reconnect
                                // with changed endpoint), the old tunnel's completion must not
                                // affect the new connection's state.
                                if (Volatile.Read(ref _client) == tunnelRef)
                                {
                                    UpdateState(TunnelConnectionState.Disconnected, "socketCompletion");
                                }
                                else
                                {
                                    _logger.LogDebug("Replaced tunnel completed - ignoring state change (a newer tunnel is active)");
                                }
                            }, TaskScheduler.Default);

                            UpdateState(TunnelConnectionState.Connected);
                        }
                        else
                        {
                            await SetTunnelAsync(null);
                        }
                    }
                    finally
                    {
                        if (!tunnelOwnsSocket)
                        {
                            websocket.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                // This worker generation was superseded (Reset cancelled it) while connecting.
                // A newer worker now owns the connection state, so do not dispose its tunnel or
                // overwrite its state here; the failed attempt's own socket was released above.
                _logger.LogDebug(ex, "Superseded connection attempt cancelled; leaving state to the active worker.");
            }
            catch
            {
                await SetTunnelAsync(null);
                UpdateState(TunnelConnectionState.Error);
            }
        }

        private async Task SetTunnelAsync(TunnelClient? tunnel)
        {
            var oldClient = Interlocked.Exchange(ref _client, tunnel);
            if (oldClient != null)
                await oldClient.DisposeAsync();
        }

        internal void UpdateState(TunnelConnectionState newState, [CallerMemberName] string? caller = default)
        {
            if (_state != newState)
            {
                if (_state == TunnelConnectionState.Connected || newState == TunnelConnectionState.Error)
                {
                    _logger.LogInformation("Tunnel connection state changed from {State} to {NewState} by {Caller}", _state, newState, caller);
                }
                else
                {
                    _logger.LogTrace("Tunnel connection state changed from {State} to {NewState} by {Caller}", _state, newState, caller);
                }
                _state = newState;
                ConnectionStateChanged?.Invoke(this, newState);
            }
        }

        private async Task TryFetchErrorResponseBodyAsync(string wsUrl, CancellationToken cancellationToken)
        {
            try
            {
                var httpUrl = wsUrl.Replace("ws://", "http://").Replace("wss://", "https://");
                using var httpClient = _tunnelClientFactory.CreateHttpClient();
                // Bound this diagnostic fetch: it must never keep a superseded reconnect attempt
                // alive. The cancellation token lets a Reset() abort it promptly, and the timeout
                // caps it well below the default 100s when the endpoint is unreachable/black-holed.
                if (httpClient.Timeout == Timeout.InfiniteTimeSpan || httpClient.Timeout > TimeSpan.FromSeconds(10))
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                }
                using var response = await httpClient.GetAsync(httpUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    try
                    {
                        LastErrorResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                        if (!string.IsNullOrWhiteSpace(LastErrorResponseBody))
                        {
                            _logger.LogDebug("Error response body from {HttpUrl}: {Body}", httpUrl, LastErrorResponseBody);
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // WebSocket endpoint or connection terminated - LastErrorResponseBody remains empty
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Reconnect attempt was superseded; abort the diagnostic fetch quietly.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch error response from {WsUrl}: {Message}", wsUrl, ex.Message);
            }
        }
    }
}
