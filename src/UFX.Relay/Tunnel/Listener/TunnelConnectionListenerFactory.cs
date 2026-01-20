using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel.Listener;

public class TunnelConnectionListenerFactory(ITunnelIdProvider tunnelIdProvider, ITunnelClientManager tunnelManager, ITunnelListenerOptionsStore options, ILoggerFactory loggerFactory) : IConnectionListenerFactory
{
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not TunnelEndpoint tunnelEndpoint) throw new NotSupportedException($"{nameof(TunnelEndpoint)} is required");
        var listener = new TunnelConnectionListener(tunnelEndpoint, tunnelIdProvider, tunnelManager, options, loggerFactory.CreateLogger<TunnelConnectionListener>());
        await listener.BindAsync();
        return listener;
    }
}