using System.Net.WebSockets;

namespace ReverseTunnel.Yarp.Tunnel.Transport;

internal static class WebSocketDiagnostics
{
    /// <summary>
    /// Describes a WebSocket for the connection log. <c>Aborted</c> with no close status means the
    /// path went silent; a close status means the peer closed deliberately.
    /// </summary>
    public static string Describe(WebSocket webSocket) =>
        $"state={webSocket.State}, " +
        $"closeStatus={webSocket.CloseStatus?.ToString() ?? "none"}, " +
        $"closeDescription={webSocket.CloseStatusDescription ?? "none"}";
}
