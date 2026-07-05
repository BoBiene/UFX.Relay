using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Abstractions;

public interface ITunnelHostManager
{
    Task<Tunnel.Tunnel?> GetOrCreateTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default);
    Task StartTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default);
    Task StartTunnelAsync(TunnelTransportConnection connection, string tunnelId, HttpContext? context, TunnelTransportKind transportKind, string? connectionId = null, CancellationToken cancellationToken = default);
}