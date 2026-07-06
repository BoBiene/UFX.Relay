namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class TunnelTransportConnection(
    Stream stream,
    Uri? uri = null,
    Func<ValueTask>? closeAsync = null,
    Action? dispose = null) : IAsyncDisposable, IDisposable
{
    public Stream Stream { get; } = stream;
    public Uri? Uri { get; } = uri;

    public void Dispose()
    {
        dispose?.Invoke();
        Stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (closeAsync is not null)
        {
            await closeAsync().ConfigureAwait(false);
        }

        if (Stream is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            Stream.Dispose();
        }
    }
}
