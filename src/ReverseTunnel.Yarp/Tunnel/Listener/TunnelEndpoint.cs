using System.Net;
using ReverseTunnel.Yarp.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel.Listener;

public class TunnelEndpoint : EndPoint
{
    public ITunnelClientManager? TunnelClientManager { get; set; }
    public string? TunnelId { get; set; }
    public Tunnel? Tunnel { get => TunnelClientManager?.Tunnel; }
    // Note: Hacky way to return the tunnel:// prefix to show up in the 'Now listening on' message as it's prefixed with http://
    public override string ToString() => ("\x1b[7Dtunnel://" + (Tunnel?.Uri?.Host ?? $"{TunnelId}")).PadRight(12);
}