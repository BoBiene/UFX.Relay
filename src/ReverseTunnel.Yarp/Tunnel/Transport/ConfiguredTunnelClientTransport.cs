using ReverseTunnel.Yarp.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class ConfiguredTunnelClientTransport(
    ITunnelClientOptionsStore optionsStore,
    IEnumerable<ITunnelClientTransport> transports) : ITunnelClientTransport
{
    private readonly IReadOnlyDictionary<TunnelTransportKind, ITunnelClientTransport> transportByKind =
        transports.ToDictionary(transport => transport.Kind);

    public TunnelTransportKind Kind => optionsStore.Current.Transport;

    public ValueTask<TunnelTransportConnection?> ConnectAsync(
        TunnelClientTransportContext context,
        CancellationToken cancellationToken)
    {
        if (!transportByKind.TryGetValue(context.Options.Transport, out var transport))
        {
            throw new TunnelTransportException($"Tunnel transport '{context.Options.Transport}' is not registered.");
        }

        return transport.ConnectAsync(context, cancellationToken);
    }
}