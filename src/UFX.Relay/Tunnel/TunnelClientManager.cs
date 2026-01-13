using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using UFX.Relay.Abstractions;
using UFX.Relay.Tunnel.Listener;

namespace UFX.Relay.Tunnel
{

    public class TunnelClientManager : ITunnelClientManager
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
        private bool _optionsChanged = false;
        private bool _stepdownErrorLogging = false;
        private readonly Timer _reconnectTimer;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        public event EventHandler<TunnelConnectionState>? ConnectionStateChanged;

        public TunnelClient? Tunnel => _client;

        public TunnelClientManager(ITunnelClientOptionsStore optionsStore, IOptions<TunnelListenerOptions> listenerOptions, ITunnelClientFactory tunnelClientFactory, ILogger<TunnelClientManager> logger)
        {
            _optionsStore = optionsStore;
            _listenerOptions = listenerOptions;
            _tunnelClientFactory = tunnelClientFactory;
            _logger = logger;

            _state = TunnelConnectionState.Disconnected;
            _reconnectTimer = new Timer(ReconnectLoop, null, TimeSpan.Zero, listenerOptions.Value.ReconnectInterval);

            _optionsStore.OptionsChanged += (_, _) =>
            {
                _optionsChanged = true;
                _logger.LogInformation("options has changed, trigger a reconnect...");
                TriggerReconnect();
            };
        }

        private void TriggerReconnect()
        {
            _reconnectTimer.Change(TimeSpan.Zero, _listenerOptions.Value.ReconnectInterval);
        }

        public TunnelConnectionState ConnectionState => _state;

        public bool IsEnabled => _optionsStore.Current.IsEnabled;

        private async void ReconnectLoop(object? state)
        {
            if (_optionsChanged)
            {
                _optionsChanged = false;
                if (_optionsStore.Current.IsEnabled)
                {
                    await ConnectInternalAsync(_cancellationTokenSource.Token);
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
                    await SetTunnelAsync(null);
                    UpdateState(TunnelConnectionState.Error);
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
                        LastConnectErrorMessage = "Connection timed out";
                        LastConnectStatusCode = 0;
                        UpdateState(TunnelConnectionState.Disconnected);
                    }
                    catch (WebSocketException ex) when (ex.InnerException is HttpRequestException httpRequestException)
                    {
                        LastConnectErrorMessage = httpRequestException.Message;
                        LastConnectStatusCode = (int?)websocket.HttpStatusCode;
                        await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri);
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
                        await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri);
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
                        tunnel.Completion.ContinueWith(_ =>
                        {
                            UpdateState(TunnelConnectionState.Disconnected, "socketCompletion");
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

        private void UpdateState(TunnelConnectionState newState, [CallerMemberName] string? caller = default)
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

        private async Task TryFetchErrorResponseBodyAsync(string wsUrl)
        {
            try
            {
                var httpUrl = wsUrl.Replace("ws://", "http://").Replace("wss://", "https://");
                using var httpClient = _tunnelClientFactory.CreateHttpClient();
                var response = await httpClient.GetAsync(httpUrl);
                if (!response.IsSuccessStatusCode)
                {
                    LastErrorResponseBody = await response.Content.ReadAsStringAsync();

                    _logger.LogDebug("Error response body from {HttpUrl}: {Body}", httpUrl, LastErrorResponseBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch error response body from {WsUrl}: {Message}", wsUrl, ex.Message);
                LastConnectErrorMessage = ex.Message;
            }

        }
    }
}