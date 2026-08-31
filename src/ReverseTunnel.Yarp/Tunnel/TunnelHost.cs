using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel;

//TODO: Add collection that tracks multiple transport connections
// Expose a method that adds a transport connection to the collection
public class TunnelHost(TunnelTransportConnection connection, MultiplexingStream stream, ILogger? logger = null)
    : Tunnel(stream, logger)
{
    protected override bool IsTransportAlive => connection.IsAlive;

    public override string DescribeTransport() => connection.DescribeState();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
