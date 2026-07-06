using System.Net.WebSockets;
using Nerdbank.Streams;

namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class WebSocketTunnelServerTransport : ITunnelServerTransport
{
    public TunnelTransportKind Kind => TunnelTransportKind.WebSocket;

    public bool CanAccept(HttpContext context) => context.WebSockets.IsWebSocketRequest;

    public async ValueTask<TunnelTransportConnection> AcceptAsync(
        HttpContext context,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var uri = new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}");
        return new TunnelTransportConnection(
            webSocket.AsStream(),
            uri,
            async () =>
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                webSocket.Dispose();
            },
            webSocket.Dispose);
    }
}
