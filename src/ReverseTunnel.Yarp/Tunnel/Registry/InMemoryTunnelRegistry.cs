using System.Collections.Concurrent;

namespace ReverseTunnel.Yarp.Tunnel.Registry;

public sealed class InMemoryTunnelRegistry : ITunnelRegistry
{
    private readonly ConcurrentDictionary<string, TunnelRegistration> registrations = new(StringComparer.Ordinal);

    public ValueTask RegisterAsync(TunnelRegistration registration, CancellationToken cancellationToken)
    {
        registrations[registration.TunnelId] = registration;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TunnelRegistration?> ResolveAsync(string tunnelId, CancellationToken cancellationToken)
    {
        if (!registrations.TryGetValue(tunnelId, out var registration))
        {
            return ValueTask.FromResult<TunnelRegistration?>(null);
        }

        if (registration.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            registrations.TryRemove(new KeyValuePair<string, TunnelRegistration>(tunnelId, registration));
            return ValueTask.FromResult<TunnelRegistration?>(null);
        }

        return ValueTask.FromResult<TunnelRegistration?>(registration);
    }

    public ValueTask RenewAsync(string tunnelId, string instanceId, CancellationToken cancellationToken)
    {
        registrations.AddOrUpdate(
            tunnelId,
            static _ => throw new KeyNotFoundException("Tunnel registration does not exist."),
            (_, current) => current.InstanceId == instanceId
                ? current with { LastSeen = DateTimeOffset.UtcNow }
                : current);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnregisterAsync(string tunnelId, string instanceId, CancellationToken cancellationToken)
    {
        if (registrations.TryGetValue(tunnelId, out var registration) &&
            registration.InstanceId == instanceId)
        {
            registrations.TryRemove(new KeyValuePair<string, TunnelRegistration>(tunnelId, registration));
        }

        return ValueTask.CompletedTask;
    }
}
