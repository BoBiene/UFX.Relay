using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ReverseTunnel.Yarp.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel
{
    public class TunnelCollection : ITunnelCollection, ITunnelCollectionProvider
    {
        protected virtual ConcurrentDictionary<string, Tunnel> Collection { get; init; } = new();

        public virtual Tunnel AddOrUpdate(string tunnelId, Func<string, Tunnel> addValueFactory, Func<string, Tunnel, Tunnel> updateValueFactory)
          => Collection.AddOrUpdate(tunnelId, addValueFactory, updateValueFactory);

        public virtual Tunnel GetOrAdd(string tunnelId, Tunnel tunnel)
            => Collection.GetOrAdd(tunnelId, tunnel);

        public virtual Tunnel GetOrAdd(string tunnelId, Func<string, Tunnel> tunnelFactory)
            => Collection.GetOrAdd(tunnelId, tunnelFactory);

        public virtual Tunnel GetOrAdd<TArg>(string tunnelId, Func<string, TArg, Tunnel> tunnelFactory, TArg factoryArgument)
            => Collection.GetOrAdd(tunnelId, tunnelFactory, factoryArgument);

        public virtual bool TryGetTunnel(string tunnelId, [MaybeNullWhen(false)] out Tunnel tunnel)
            => Collection.TryGetValue(tunnelId, out tunnel);

        public virtual bool TryRemoveTunnel((string TunnelId, Tunnel Tunnel) item)
            => Collection.TryRemove(new KeyValuePair<string, Tunnel>(item.TunnelId, item.Tunnel));

        public virtual bool TryRemoveTunnelById(string tunnelId, [MaybeNullWhen(false)] out Tunnel tunnel)
            => Collection.TryRemove(tunnelId, out tunnel);

        public virtual IEnumerator<(string TunnelId, Tunnel Tunnel)> GetEnumerator()
            => Collection.Select(item => (item.Key, item.Value)).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();

        public virtual Task<ITunnelCollection> GetTunnelCollectionAsync(HttpContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ITunnelCollection>(this);
        }
    }
}
