namespace ReverseTunnel.Yarp.Tunnel.Transport;

public interface ITunnelServerTransport
{
    TunnelTransportKind Kind { get; }

    bool CanAccept(HttpContext context);

    ValueTask<TunnelTransportConnection> AcceptAsync(
        HttpContext context,
        string tunnelId,
        CancellationToken cancellationToken);
}
