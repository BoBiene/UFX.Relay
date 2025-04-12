namespace UFX.Relay.Abstractions;

public interface ITunnelHostManager
{
    Task<bool> IsTunnelConnectedAsync(string tunnelId, CancellationToken cancellationToken = default);
    Task<Tunnel.Tunnel?> GetTunnelAsync(string tunnelId, CancellationToken cancellationToken = default);
    Task<Tunnel.Tunnel?> GetOrCreateTunnelAsync(string tunnelId, CancellationToken cancellationToken = default);
    Task StartTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default);

    IEnumerable<Tunnel.Tunnel> GetConnectedTunnels(CancellationToken cancellationToken = default);
}