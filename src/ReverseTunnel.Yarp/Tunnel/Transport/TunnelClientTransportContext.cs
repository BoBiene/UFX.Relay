namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class TunnelClientTransportContext(TunnelClientOptions options, string tunnelId)
{
    public TunnelClientOptions Options { get; } = options;
    public string TunnelId { get; } = tunnelId;
}
