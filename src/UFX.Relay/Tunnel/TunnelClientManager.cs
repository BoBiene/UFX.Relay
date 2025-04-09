using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using System.Net.WebSockets;
using UFX.Relay.Abstractions;
using UFX.Relay.Tunnel.Listener;

namespace UFX.Relay.Tunnel
{

    public class TunnelClientManager : ITunnelClientManager
    {
        private readonly ILogger<TunnelClientManager> _logger;
        private readonly ITunnelClientOptionsStore _optionsStore;
        private readonly ITunnelClientFactory _tunnelClientFactory;
        private TunnelConnectionState _state;
        private TunnelClient? _client;
        private bool _optionsChanged = false;
        private bool _stepdownErrorLogging = false;
        private readonly Timer _reconnectTimer;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        public event EventHandler<TunnelConnectionState>? ConnectionStateChanged;

        public TunnelClient? Tunnel => _client;

        public TunnelClientManager(ITunnelClientOptionsStore optionsStore, IOptions<TunnelListenerOptions> listenerOptions, ITunnelClientFactory tunnelClientFactory, ILogger<TunnelClientManager> logger)
        {
            _optionsStore = optionsStore;
            _tunnelClientFactory = tunnelClientFactory;
            _logger = logger;
            _optionsStore.OptionsChanged += (_, _) => _optionsChanged = true;
            _state = TunnelConnectionState.Disconnected;
            _reconnectTimer = new Timer(ReconnectLoop, null, TimeSpan.Zero, listenerOptions.Value.ReconnectInterval);
        }

        public TunnelConnectionState ConnectionState => _state;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_optionsStore.Current.IsEnabled)
            {
                await ConnectInternalAsync(cancellationToken);
            }
            else
            {
                // If the tunnel is disabled, ensure we are in a disconnected state
                if (_state != TunnelConnectionState.Disconnected)
                {
                    await StopAsync(cancellationToken);
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_client != null)
            {
                await _client.DisposeAsync();
                _client = null;
                UpdateState(TunnelConnectionState.Disconnected);
            }
        }

        public async Task ReconnectAsync(CancellationToken cancellationToken = default)
        {
            await StopAsync(cancellationToken);
            await StartAsync(cancellationToken);
        }

        private async void ReconnectLoop(object? state)
        {
            if (_optionsChanged)
            {
                _optionsChanged = false;
                await ReconnectAsync(_cancellationTokenSource.Token);
            }
            else if (_state == TunnelConnectionState.Connected || _state == TunnelConnectionState.Connecting)
            {
                // we are already connected or connecting, do nothing
            }
            else
            {
                await ConnectInternalAsync(_cancellationTokenSource.Token);
            }
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
                    UpdateState(TunnelConnectionState.Error);
                    await SetTunnelAsync(null);
                }
                else
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
                        websocket.Dispose();
                        UpdateState(TunnelConnectionState.Disconnected);
                    }
                    catch (WebSocketException ex) when (ex.InnerException is HttpRequestException httpRequestException)
                    {
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
                        _logger.LogDebug(ex, "Websocket Error: {Uri}, {Message}", uri, ex.Message);
                        UpdateState(TunnelConnectionState.Error);
                    }

                    if (connected)
                    {
                        _logger.LogInformation("Connected to {Uri}", uri);
                        var stream = await MultiplexingStream.CreateAsync(websocket.AsStream(), new MultiplexingStream.Options
                        {
                            ProtocolMajorVersion = 3
                        }, cancellationToken);

                        var tunnel = new TunnelClient(websocket, stream) { Uri = uri };
                        tunnel.Completion.ContinueWith(_ =>
                        {
                            UpdateState(TunnelConnectionState.Disconnected);
                        }, TaskScheduler.Default);

                        await SetTunnelAsync(tunnel);
                        UpdateState(TunnelConnectionState.Connected);
                    }
                    else
                    {
                        await SetTunnelAsync(null);
                    }
                }
            }
            catch
            {
                UpdateState(TunnelConnectionState.Error);
                await SetTunnelAsync(null);
            }
        }

        private async Task SetTunnelAsync(TunnelClient? tunnel)
        {
            var oldClient = Interlocked.Exchange(ref _client, tunnel);
            if (oldClient != null)
                await oldClient.DisposeAsync();
        }

        private void UpdateState(TunnelConnectionState newState)
        {
            if (_state != newState)
            {
                if (_state == TunnelConnectionState.Connected || newState == TunnelConnectionState.Error)
                {
                    _logger.LogInformation("Tunnel connection state changed from {State} to {NewState}", _state, newState);
                }
                else
                {
                    _logger.LogTrace("Tunnel connection state changed from {State} to {NewState}", _state, newState);
                }
                _state = newState;
                ConnectionStateChanged?.Invoke(this, newState);
            }
        }
    }
}