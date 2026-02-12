using System.Net.WebSockets;
using UFX.Relay.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel;

public sealed class HostTunnelClientFactory : ITunnelClientFactory
{
    public ValueTask<ClientWebSocket?> CreateAsync() => new();

    public ValueTask<Uri> GetUriAsync()
    {
        throw new NotImplementedException();
    }

    public HttpClient CreateHttpClient()
    {
        return new HttpClient();
    }
}