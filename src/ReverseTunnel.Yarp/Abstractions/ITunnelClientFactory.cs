using System.Net.WebSockets;

namespace ReverseTunnel.Yarp.Abstractions;

public interface ITunnelClientFactory
{
    ValueTask<ClientWebSocket?> CreateAsync();
    ValueTask<Uri> GetUriAsync();
    HttpClient CreateHttpClient();
}
