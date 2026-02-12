using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Abstractions;

namespace UFX.Relay.Tunnel.Listener;

public class ListenerTunnelIdProvider(IOptions<TunnelListenerOptions> listenerOptions, ITunnelClientOptionsStore clientOptionsStore) : ITunnelIdProvider
{
    public ValueTask<string?> GetTunnelIdAsync()
    {
        return new ValueTask<string?>(
            listenerOptions.Value.DefaultTunnelId
            ?? clientOptionsStore.Current.TunnelId
            ?? (clientOptionsStore.Current.TunnelHost != null ? new Uri(clientOptionsStore.Current.TunnelHost).GetTunnelIdFromHost() : null));
    }
}