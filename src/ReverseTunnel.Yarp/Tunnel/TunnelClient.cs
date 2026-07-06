using Nerdbank.Streams;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel;

//TODO: Add collection that tracks multiple client transport connections
// Pass a factory to the constructor that creates a new transport connection instance
// Automatically create a new connection when a channel limit is reached
// Would need a low/high watermark for the creation/closing of transport connections
// What would be the best way to select an existing transport connection to use?
public class TunnelClient(TunnelTransportConnection connection, MultiplexingStream stream) : Tunnel(stream)
{
    protected override async ValueTask DisposeAsyncCore()
    {
        await connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore();
    }
}