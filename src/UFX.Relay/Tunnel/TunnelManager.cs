using System.Collections.Concurrent;
using System.Net.WebSockets;
using Nerdbank.Streams;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel;

public class TunnelHostManager(ILogger<TunnelHostManager> logger) : ITunnelHostManager
{
    private readonly ConcurrentDictionary<string, Tunnel> tunnels = new();
    public Task<Tunnel?> GetOrCreateTunnelAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        if (tunnels.TryGetValue(tunnelId, out var existingTunnel))
            return Task.FromResult<Tunnel?>(existingTunnel);

        return Task.FromResult<Tunnel?>(null);
    }

    public async Task StartTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default)
    {
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await using var stream = await MultiplexingStream.CreateAsync(webSocket.AsStream(), new MultiplexingStream.Options
        {
            ProtocolMajorVersion = 3
        }, cancellationToken);
        var tunnel = new TunnelHost(webSocket, stream) { Uri = new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}") };
        tunnels.AddOrUpdate(tunnelId, _ => tunnel, (_, oldTunnel) =>
        {
            oldTunnel.Dispose();
            return tunnel;
        });
        logger.LogDebug("Tunnel connected: {TunnelId} from {RemoteIpAddress}:{RemotePort}", tunnelId, context.Connection.RemoteIpAddress, context.Connection.RemotePort);
        try
        {
            await stream.Completion;
        }
        catch (Exception e)
        {
            logger.LogDebug("Tunnel: {TunnelId}, Message: {Message}", tunnelId, e.Message);
        }
        finally
        {
            tunnels.TryRemove(new KeyValuePair<string, Tunnel>(tunnelId, tunnel));
        }
    }
}