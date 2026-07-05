using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel;

public sealed class ReverseTunnelOptions
{
    public TunnelTransportKind Transport { get; set; } = TunnelTransportKind.WebSocket;
    public string? InstanceId { get; set; }
    public Uri? InternalEndpoint { get; set; }
    public TimeSpan RegistryTtl { get; set; } = TimeSpan.FromMinutes(2);
    public string ForwardedByHeader { get; set; } = "X-ReverseTunnel-Forwarded-By";
}
