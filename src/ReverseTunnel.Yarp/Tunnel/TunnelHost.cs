using Nerdbank.Streams;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel;

//TODO: Add collection that tracks multiple transport connections
// Expose a method that adds a transport connection to the collection
public class TunnelHost(TunnelTransportConnection connection, MultiplexingStream stream) : Tunnel(stream)
{
    protected override async ValueTask DisposeAsyncCore()
    {
        await connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore();
    }
}