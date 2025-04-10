
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using System.Net.WebSockets;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel
{
    public class TunnelHostAggregatedManager(ILogger<TunnelHostAggregatedManager> logger, ITunnelClientFactory tunnelClientFactory) 
        : TunnelHostManager(logger)
    {

        public override async Task<Tunnel?> GetOrCreateTunnelAsync(string tunnelId, CancellationToken cancellationToken = default)
        {
            if (tunnels.TryGetValue(tunnelId, out var existingTunnel)) return existingTunnel;
            if (tunnelClientFactory == null) return null;
            var websocket = await tunnelClientFactory.CreateAsync();
            if (websocket == null) return null;
            var uri = await tunnelClientFactory.GetUriAsync();
            bool connected = false;
            while (!connected)
            {
                try
                {
                    await websocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                    connected = true;
                }
                catch (TaskCanceledException)
                {
                    websocket.Dispose();
                    return null;
                }
                catch (WebSocketException ex)
                {
                    logger.LogDebug("Websocket Error: {Uri}, {Message}", uri, ex.Message);
                    await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                    websocket = await tunnelClientFactory.CreateAsync() ?? throw new NullReferenceException("Websocket is null");
                }
            }
            logger.LogInformation("Connected to {Uri}", uri);
            var stream = await MultiplexingStream.CreateAsync(websocket.AsStream(), new MultiplexingStream.Options
            {
                ProtocolMajorVersion = 3
            }, cancellationToken);
            var tunnel = new TunnelClient(websocket, stream) { Uri = uri };
            //TODO: Reconnect websocket if closed after initial connection if tunnel has not been disposed
            tunnel.Completion.ContinueWith(_ =>
            {
                logger.LogDebug("Removing tunnel {TunnelId}, uri: {Uri}", tunnelId, uri);
                return tunnels.TryRemove(new KeyValuePair<string, Tunnel>(tunnelId, tunnel));
            }, TaskScheduler.Default);
            return tunnels.GetOrAdd(tunnelId, tunnel);
        }
    }
}
