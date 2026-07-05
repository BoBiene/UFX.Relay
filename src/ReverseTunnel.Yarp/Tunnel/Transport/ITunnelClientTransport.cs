namespace ReverseTunnel.Yarp.Tunnel.Transport;

public interface ITunnelClientTransport
{
    TunnelTransportKind Kind { get; }

    ValueTask<TunnelTransportConnection?> ConnectAsync(
        TunnelClientTransportContext context,
        CancellationToken cancellationToken);
}
