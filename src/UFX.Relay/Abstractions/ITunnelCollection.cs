using Microsoft.AspNetCore.DataProtection.KeyManagement;
using System.Diagnostics.CodeAnalysis;

namespace UFX.Relay.Abstractions
{
    public interface ITunnelCollection : IEnumerable<(string TunnelId, Tunnel.Tunnel Tunnel)>
    {
        bool TryGetTunnel(string tunnelId, [MaybeNullWhen(false)] out Tunnel.Tunnel tunnel);
        bool TryRemoveTunnel((string TunnelId, Tunnel.Tunnel Tunnel) item);
        bool TryRemoveTunnelById(string tunnelId, [MaybeNullWhen(false)] out Tunnel.Tunnel tunnel);

        Tunnel.Tunnel GetOrAdd(string tunnelId, Tunnel.Tunnel tunnel);
        Tunnel.Tunnel GetOrAdd(string tunnelId, Func<string, Tunnel.Tunnel> tunnelFactory);
        Tunnel.Tunnel GetOrAdd<TArg>(string tunnelId, Func<string, TArg, Tunnel.Tunnel> tunnelFactory, TArg factoryArgument);
        Tunnel.Tunnel AddOrUpdate(string tunnelId, Func<string, Tunnel.Tunnel> addValueFactory, Func<string, Tunnel.Tunnel, Tunnel.Tunnel> updateValueFactory);
    }
}
