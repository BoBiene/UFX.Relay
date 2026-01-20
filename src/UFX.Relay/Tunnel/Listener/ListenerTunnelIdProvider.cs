using Microsoft.Extensions.Options;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel.Listener;

public class ListenerTunnelIdProvider(ITunnelListenerOptionsStore listenerOptions, ITunnelClientOptionsStore clientOptionsStore) : ITunnelIdProvider
{
    public ValueTask<string?> GetTunnelIdAsync()
    {
        return new ValueTask<string?>(
            listenerOptions.Current.DefaultTunnelId
            ?? clientOptionsStore.Current.TunnelId
            ?? (clientOptionsStore.Current.TunnelHost != null ? new Uri(clientOptionsStore.Current.TunnelHost).GetTunnelIdFromHost() : null));
    }
}