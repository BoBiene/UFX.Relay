using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
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

                int addedFlags = OptionsChangedBit | (isEndpointChange ? EndpointChangedBit : 0);
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
            int flags = Interlocked.Exchange(ref _pendingOptionsChange, 0);
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
                if (_state != TunnelConnectionState.Disconnected)
                {
                    await SetTunnelAsync(null);
                    UpdateState(TunnelConnectionState.Disconnected);
                }
            }
            else if (_state == TunnelConnectionState.Connected || _state == TunnelConnectionState.Connecting)
            {
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

                var tunnelId = _optionsStore.Current.TunnelId;
                if (string.IsNullOrWhiteSpace(tunnelId))
                {
                    _logger.LogWarning("Tunnel connection failed because no tunnel id is configured.");
                    await SetTunnelAsync(null);
                    UpdateState(TunnelConnectionState.Error);
                    return;
                }

                var connection = await _clientTransport.ConnectAsync(
                    new TunnelClientTransportContext(_optionsStore.Current, tunnelId),
                    cancellationToken).ConfigureAwait(false);

                if (connection == null)
                {
                    _logger.LogWarning("Tunnel transport connection failed (transport returned null).");
                    await SetTunnelAsync(null);
                    UpdateState(TunnelConnectionState.Error);
                    return;
                }

                LastErrorResponseBody = string.Empty;
                LastConnectErrorMessage = string.Empty;
                LastConnectStatusCode = null;
                _stepdownErrorLogging = false;
                _logger.LogInformation("Connected to {Uri}", connection.Uri);
                var stream = await MultiplexingStream.CreateAsync(connection.Stream, new MultiplexingStream.Options
                {
                    ProtocolMajorVersion = 3
                }, cancellationToken).ConfigureAwait(false);

                var tunnel = new TunnelClient(connection, stream) { Uri = connection.Uri };

                await SetTunnelAsync(tunnel);

                var tunnelRef = tunnel;
                _ = tunnel.Completion.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogDebug(t.Exception, "Tunnel stream ended with error");
                    }
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
            catch (TunnelTransportException ex)
            {
                await SetTunnelAsync(null);
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
    }
}