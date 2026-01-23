using System.Collections.ObjectModel;
using System.Net.WebSockets;

namespace UFX.Relay.Tunnel;

public sealed record TunnelClientOptions
{
    public string? TunnelId { get; init; }
    public string? TunnelHost { get; init; }
    public string TunnelPathTemplate { get; init; } = "/tunnel/{0}";
    public bool IsEnabled { get; init; } = true;
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    public Action<ClientWebSocketOptions>? WebSocketOptions { get; init; }
}