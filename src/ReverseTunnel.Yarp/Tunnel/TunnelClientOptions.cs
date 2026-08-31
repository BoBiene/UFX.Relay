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

    /// <summary>How often the transport sends a keep-alive ping.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the transport waits for a keep-alive answer before aborting the connection.
    /// Applied only on .NET 9 and later, where <c>ClientWebSocketOptions.KeepAliveTimeout</c>
    /// exists. On net8.0 there is no client-side liveness detection at all.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(15);
}