using System.Net.WebSockets;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel;

public sealed class ClientTunnelClientFactory(ITunnelClientOptionsStore options, ITunnelIdProvider tunnelIdProvider) : ITunnelClientFactory
{
    public async ValueTask<ClientWebSocket?> CreateAsync()
    {
        var tunnelId = await tunnelIdProvider.GetTunnelIdAsync();
        if (tunnelId == null) return null;
        var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        options.Current.WebSocketOptions?.Invoke(webSocket.Options);
        return webSocket;
    }

    public async ValueTask<Uri> GetUriAsync()
    {
        if (options.Current.TunnelHost == null) throw new ArgumentNullException(nameof(options.Current.TunnelHost));
        var tunnelId = await tunnelIdProvider.GetTunnelIdAsync();
        var uri = new UriBuilder(options.Current.TunnelHost)
        {
            Path = string.Format(options.Current.TunnelPathTemplate, tunnelId)
        };
        return uri.Uri;
    }
}