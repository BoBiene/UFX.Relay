namespace ReverseTunnel.Yarp.Tunnel.Registry;

public interface ITunnelRegistry
{
    ValueTask RegisterAsync(TunnelRegistration registration, CancellationToken cancellationToken);

    ValueTask<TunnelRegistration?> ResolveAsync(string tunnelId, CancellationToken cancellationToken);

    ValueTask RenewAsync(string tunnelId, string instanceId, CancellationToken cancellationToken);

    ValueTask UnregisterAsync(string tunnelId, string instanceId, CancellationToken cancellationToken);
}
