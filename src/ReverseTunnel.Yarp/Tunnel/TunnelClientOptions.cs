using System.Collections.ObjectModel;
using System.Net.WebSockets;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel;

public sealed record TunnelClientOptions
{
    public string? TunnelId { get; init; }
    public string? TunnelHost { get; init; }
    public string TunnelPathTemplate { get; init; } = "/tunnel/{0}";
    public TunnelTransportKind Transport { get; init; } = TunnelTransportKind.WebSocket;
    public bool IsEnabled { get; init; } = true;
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    public Action<ClientWebSocketOptions>? WebSocketOptions { get; init; }
}