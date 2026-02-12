using System.Collections.Concurrent;
using System.Net.WebSockets;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel;

public class TunnelHostManager(ILogger<TunnelHostManager> logger, ITunnelCollectionProvider tunnelCollectionProvider) : ITunnelHostManager
{
    public virtual async Task<Tunnel?> GetOrCreateTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default)
    {
        var tunnels = await tunnelCollectionProvider.GetTunnelCollectionAsync(context, cancellationToken);
        if (tunnels.TryGetTunnel(tunnelId, out var existingTunnel))
            return existingTunnel;

        return null;
    }


    public async Task StartTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default)
    {
        var tunnels = await tunnelCollectionProvider.GetTunnelCollectionAsync(context, cancellationToken);

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
            logger.LogDebug(e, "Tunnel: {TunnelId}, Message: {Message}", tunnelId, e.Message);
        }
        finally
        {
            tunnels.TryRemoveTunnel((tunnelId, tunnel));
            logger.LogDebug("Tunnel disconnected: {TunnelId} from {RemoteIpAddress}:{RemotePort}", tunnelId, context.Connection.RemoteIpAddress, context.Connection.RemotePort);
        }
    }
}