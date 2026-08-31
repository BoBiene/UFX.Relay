using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel
{
    public class TunnelClientManager : ITunnelClientManager, IDisposable
    {
        private readonly ILogger<TunnelClientManager> _logger;
        private readonly ITunnelClientOptionsStore _optionsStore;
        private readonly ITunnelClientTransport _clientTransport;
        private readonly IOptions<TunnelListenerOptions> _listenerOptions;
        public string LastConnectErrorMessage { get; private set; } = string.Empty;
        public int? LastConnectStatusCode { get; private set; } = null;
        public string LastErrorResponseBody { get; private set; } = string.Empty;
        private TunnelConnectionState _state;
        private TunnelClient? _client;
        private int _pendingOptionsChange = 0;
        private const int OptionsChangedBit = 1;
        private const int EndpointChangedBit = 2;
        private bool _stepdownErrorLogging = false;
        private long _connectingSinceTimestamp = Stopwatch.GetTimestamp();
        private readonly WorkerWithBackoff _reconnectWorker;
        public event EventHandler<TunnelConnectionState>? ConnectionStateChanged;

        public TunnelClient? Tunnel => _client;

        internal TunnelConnectionState State
        {
            get => _state;
            set => _state = value;
        }

        internal TunnelClient? ActiveClient
        {
            get => Volatile.Read(ref _client);
            set => Volatile.Write(ref _client, value);
        }

        public TunnelClientManager(
            ITunnelClientOptionsStore optionsStore,
            IOptions<TunnelListenerOptions> listenerOptions,
            ITunnelClientFactory tunnelClientFactory,
            ILogger<TunnelClientManager> logger)
            : this(optionsStore, listenerOptions, new WebSocketTunnelClientTransport(tunnelClientFactory), logger)
        {
        }

        [ActivatorUtilitiesConstructor]
        public TunnelClientManager(
            ITunnelClientOptionsStore optionsStore,
            IOptions<TunnelListenerOptions> listenerOptions,
            ITunnelClientTransport clientTransport,
            ILogger<TunnelClientManager> logger)
        {
            _optionsStore = optionsStore;
            _listenerOptions = listenerOptions;
            _clientTransport = clientTransport;
            _logger = logger;

            _state = TunnelConnectionState.Disconnected;
            _reconnectWorker = new(listenerOptions.Value.ReconnectInterval, listenerOptions.Value.MaxReconnectInterval, ReconnectLoopAsync);

            _optionsStore.OptionsChanged += (_, args) =>
            {
                bool isEndpointChange =
                    args.OldOptions.TunnelHost != args.NewOptions.TunnelHost ||
                    args.OldOptions.TunnelId != args.NewOptions.TunnelId ||
                    args.OldOptions.IsEnabled != args.NewOptions.IsEnabled ||
                    args.OldOptions.TunnelPathTemplate != args.NewOptions.TunnelPathTemplate ||
                    args.OldOptions.Transport != args.NewOptions.Transport ||
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

        /// <summary>
        /// Corrects a state that no longer matches reality: a <c>Connecting</c> that has stopped
        /// progressing, or a <c>Connected</c> whose tunnel is gone. Both are cached in a field, so
        /// without this they would persist until something else happened to update them.
        /// </summary>
        private void ReconcileState()
        {
            if (_state == TunnelConnectionState.Connecting &&
                Stopwatch.GetElapsedTime(Volatile.Read(ref _connectingSinceTimestamp)) > _listenerOptions.Value.ConnectingWatchdogTimeout)
            {
                _logger.LogWarning(
                    "Tunnel stuck in Connecting for more than {Timeout}; forcing a retry.",
                    _listenerOptions.Value.ConnectingWatchdogTimeout);
                UpdateState(TunnelConnectionState.Disconnected, "connectingWatchdog");
                return;
            }

            if (_state != TunnelConnectionState.Connected) return;

            TunnelClient? active = Volatile.Read(ref _client);
            if (active is not null && active.IsConnected) return;

            UpdateState(TunnelConnectionState.Disconnected, "connectedReconciliation");
        }

        private async Task<bool> ReconnectLoopAsync(CancellationToken token)
        {
            // Backoff applies only after an attempt that failed.
            bool doBackoff = false;
            int flags = Interlocked.Exchange(ref _pendingOptionsChange, 0);

            ReconcileState();

            if ((flags & OptionsChangedBit) != 0)
            {
                bool endpointChanged = (flags & EndpointChangedBit) != 0;

                if (_optionsStore.Current.IsEnabled)
                {
                    if (_state == TunnelConnectionState.Connected && !endpointChanged)
                    {
                        _logger.LogDebug("Options updated (credentials refresh) while already connected - keeping existing connection");
                        doBackoff = false;
                    }
                    else
                    {
                        doBackoff = !await ConnectInternalAsync(token) && _listenerOptions.Value.EnableReconnectBackoff;
                    }
                }
                else
                {
                    PublishTunnel(null);
                    UpdateState(TunnelConnectionState.Disconnected);
                }
            }
            else if (!_optionsStore.Current.IsEnabled)
            {
                if (_state != TunnelConnectionState.Disconnected)
                {
                    PublishTunnel(null);
                    UpdateState(TunnelConnectionState.Disconnected);
                }
            }
            else if (_state == TunnelConnectionState.Connected || _state == TunnelConnectionState.Connecting)
            {
                doBackoff = false;
            }
            else
            {
                doBackoff = !await ConnectInternalAsync(token) && _listenerOptions.Value.EnableReconnectBackoff;
            }

            return doBackoff;
        }

        /// <returns><c>true</c> when the attempt reached <see cref="TunnelConnectionState.Connected"/>.</returns>
        private async Task<bool> ConnectInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                UpdateState(TunnelConnectionState.Connecting);

                var tunnelId = _optionsStore.Current.TunnelId;
                if (string.IsNullOrWhiteSpace(tunnelId))
                {
                    _logger.LogWarning("Tunnel connection failed because no tunnel id is configured.");
                    PublishTunnel(null);
                    UpdateState(TunnelConnectionState.Error);
                    return false;
                }

                var connection = await _clientTransport.ConnectAsync(
                    new TunnelClientTransportContext(_optionsStore.Current, tunnelId),
                    cancellationToken).ConfigureAwait(false);

                if (connection == null)
                {
                    _logger.LogWarning("Tunnel transport connection failed (transport returned null).");
                    PublishTunnel(null);
                    UpdateState(TunnelConnectionState.Error);
                    return false;
                }

                // The transport connection is owned by this method until it is handed to a
                // TunnelClient. On every other exit (multiplexing handshake failure or cancellation)
                // the finally block disposes it, so a connected-but-unowned transport can never leak.
                bool tunnelOwnsConnection = false;
                try
                {
                    LastErrorResponseBody = string.Empty;
                    LastConnectErrorMessage = string.Empty;
                    LastConnectStatusCode = null;
                    _stepdownErrorLogging = false;
                    _logger.LogInformation("Connected to {Uri}", connection.Uri);
                    var stream = await MultiplexingStream.CreateAsync(connection.Stream, new MultiplexingStream.Options
                    {
                        ProtocolMajorVersion = 3
                    }, cancellationToken).ConfigureAwait(false);

                    var tunnel = new TunnelClient(connection, stream, _logger)
                    {
                        Uri = connection.Uri,
                        ConnectionId = connection.ConnectionId
                    };
                    _logger.LogInformation(
                        "Tunnel connected. ConnectionId={ConnectionId}, Uri={Uri}",
                        tunnel.ConnectionId ?? "unknown",
                        connection.Uri?.ToString() ?? "unknown");

                    // The tunnel owns the transport from here, so a failure tearing down the
                    // previous connection does not make the finally below dispose this one.
                    tunnelOwnsConnection = true;

                    // Publish before attaching the completion continuation, so its guard
                    // (Volatile.Read(ref _client) == tunnelRef) sees this tunnel.
                    PublishTunnel(tunnel);

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
                    return true;
                }
                finally
                {
                    if (!tunnelOwnsConnection)
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (TunnelTransportException ex)
            {
                PublishTunnel(null);
                LastConnectErrorMessage = ex.Message;
                LastConnectStatusCode = ex.StatusCode;
                LastErrorResponseBody = ex.ResponseBody;

                if (!_stepdownErrorLogging)
                {
                    _logger.LogInformation(ex, "Failed to connect to {Uri}: {Message} (Code: {StatusCode})", ex.Uri, ex.Message, ex.StatusCode);
                    _stepdownErrorLogging = true;
                }
                else
                {
                    _logger.LogTrace("Failed to connect to {Uri}: {Message} (Code: {StatusCode})", ex.Uri, ex.Message, ex.StatusCode);
                }

                UpdateState(ex.StatusCode == 0 ? TunnelConnectionState.Disconnected : TunnelConnectionState.Error);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                // This worker generation was superseded (Reset cancelled it) while connecting.
                // A newer worker now owns the connection state, so do not dispose its tunnel or
                // overwrite its state here; this attempt's own transport was released above.
                _logger.LogDebug(ex, "Superseded connection attempt cancelled; leaving state to the active worker.");
            }
            catch
            {
                PublishTunnel(null);
                UpdateState(TunnelConnectionState.Error);
            }

            return false;
        }

        /// <summary>
        /// Publishes <paramref name="tunnel"/> as the active one and releases the previous tunnel
        /// detached, so a healthy new connection never waits on the teardown of the one it replaces.
        /// The teardown itself is bounded, see TunnelTransportConnection.CloseTimeout.
        /// </summary>
        private void PublishTunnel(TunnelClient? tunnel)
        {
            var oldClient = Interlocked.Exchange(ref _client, tunnel);
            if (oldClient is null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await oldClient.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Releasing the previous tunnel failed: {Message}", ex.Message);
                }
            });
        }

        internal void UpdateState(TunnelConnectionState newState, [CallerMemberName] string? caller = default)
        {
            if (_state != newState)
            {
                if (_state == TunnelConnectionState.Connected)
                {
                    // One event per loss. Detector says which check noticed, and the transport state
                    // says what kind of loss it was.
                    _logger.LogInformation(
                        "Tunnel connection lost. detector={Detector}, newState={NewState}, {Diagnostics}",
                        caller ?? "unknown",
                        newState,
                        Volatile.Read(ref _client)?.GetDiagnostics().ToString() ?? "no active tunnel");
                }

                if (_state == TunnelConnectionState.Connected || newState == TunnelConnectionState.Error)
                {
                    _logger.LogInformation("Tunnel connection state changed from {State} to {NewState} by {Caller}", _state, newState, caller);
                }
                else
                {
                    _logger.LogTrace("Tunnel connection state changed from {State} to {NewState} by {Caller}", _state, newState, caller);
                }
                _state = newState;
                if (newState == TunnelConnectionState.Connecting)
                {
                    Volatile.Write(ref _connectingSinceTimestamp, Stopwatch.GetTimestamp());
                }
                ConnectionStateChanged?.Invoke(this, newState);
            }
        }
    }
}
