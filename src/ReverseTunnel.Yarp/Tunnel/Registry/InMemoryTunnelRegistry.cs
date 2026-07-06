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
        // Renewing slides the expiry window forward while preserving the original TTL
        // (ExpiresAt - LastSeen). A missing entry is treated as a no-op so a renewal that
        // races an eviction does not throw; the owner will re-register on the next connect.
        if (registrations.TryGetValue(tunnelId, out var current) && current.InstanceId == instanceId)
        {
            var now = DateTimeOffset.UtcNow;
            var ttl = current.ExpiresAt - current.LastSeen;
            if (ttl <= TimeSpan.Zero)
            {
                ttl = TimeSpan.FromMinutes(2);
            }

            var renewed = current with { LastSeen = now, ExpiresAt = now.Add(ttl) };
            registrations.TryUpdate(tunnelId, renewed, current);
        }

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
