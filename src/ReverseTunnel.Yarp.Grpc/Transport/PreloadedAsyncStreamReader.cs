using Grpc.Core;

namespace ReverseTunnel.Yarp.Grpc.Transport;

internal sealed class PreloadedAsyncStreamReader<T>(T first, IAsyncStreamReader<T> inner) : IAsyncStreamReader<T>
{
    private bool hasFirst = true;

    public T Current { get; private set; } = first;

    public Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (hasFirst)
        {
            hasFirst = false;
            return Task.FromResult(true);
        }

        return inner.MoveNext(cancellationToken);
    }
}