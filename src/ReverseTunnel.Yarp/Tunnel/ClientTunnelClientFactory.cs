using System.Net.WebSockets;
using ReverseTunnel.Yarp.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel;

public sealed class ClientTunnelClientFactory(ITunnelClientOptionsStore options, ITunnelIdProvider tunnelIdProvider) : ITunnelClientFactory
{
    public async ValueTask<ClientWebSocket?> CreateAsync()
    {
        var tunnelId = await tunnelIdProvider.GetTunnelIdAsync();
        if (tunnelId == null) return null;
        var webSocket = new ClientWebSocket();
        webSocket.Options.CollectHttpResponseDetails = true;
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        
        foreach (var header in options.Current.RequestHeaders)
        {
            webSocket.Options.SetRequestHeader(header.Key, header.Value);
        }
        
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

    public HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        
        foreach (var header in options.Current.RequestHeaders)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
        
        return httpClient;
    }
}