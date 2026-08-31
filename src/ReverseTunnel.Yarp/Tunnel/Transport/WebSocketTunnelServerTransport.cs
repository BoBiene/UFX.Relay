using System.Net.WebSockets;
using Nerdbank.Streams;

namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class WebSocketTunnelServerTransport : ITunnelServerTransport
{
    public TunnelTransportKind Kind => TunnelTransportKind.WebSocket;

    public bool CanAccept(HttpContext context) => context.WebSockets.IsWebSocketRequest;

    /// <summary>How often the server sends a keep-alive ping on an accepted tunnel.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the server waits for a keep-alive answer before aborting. Requires .NET 9 or
    /// later.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async ValueTask<TunnelTransportConnection> AcceptAsync(
        HttpContext context,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        WebSocketAcceptContext acceptContext = new() { KeepAliveInterval = KeepAliveInterval };
#if NET9_0_OR_GREATER
        acceptContext.KeepAliveTimeout = KeepAliveTimeout;
#endif
        var webSocket = await context.WebSockets.AcceptWebSocketAsync(acceptContext).ConfigureAwait(false);
        var uri = new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}");
        return new TunnelTransportConnection(
            webSocket.AsStream(),
            uri,
            async () =>
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // Bounded: CloseAsync waits for the peer's close frame, which never arrives on a dead path.
                    using CancellationTokenSource closeCts = new(TunnelTransportConnection.CloseTimeout);
                    try
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token).ConfigureAwait(false);
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
            webSocket.Dispose,
            isAlive: () => webSocket.State is WebSocketState.Open or WebSocketState.CloseSent,
            describeState: () => WebSocketDiagnostics.Describe(webSocket));
    }
}
