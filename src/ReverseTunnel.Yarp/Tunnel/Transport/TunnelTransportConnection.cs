namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class TunnelTransportConnection(
    Stream stream,
    Uri? uri = null,
    Func<ValueTask>? closeAsync = null,
    Action? dispose = null,
    Func<bool>? isAlive = null,
    Func<string>? describeState = null) : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Upper bound for each step of a graceful close. A close writes a frame and may wait for the
    /// peer's answer, neither of which completes on a dead path, so every step is bounded and the
    /// connection is aborted instead.
    /// </summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    public Stream Stream { get; } = stream;
    public Uri? Uri { get; } = uri;

    /// <summary>Client-generated id for this connection, sent to the host so both sides log it.</summary>
    public string? ConnectionId { get; set; }

    /// <summary>
    /// Whether the transport reports itself usable. This is the only reliable signal for a
    /// WebSocket that aborted on its keep-alive timeout, because that does not complete the
    /// multiplexing stream.
    /// </summary>
    public bool IsAlive => isAlive?.Invoke() ?? true;

    /// <summary>
    /// The transport's own description of its state. Distinguishes an abort without a close status
    /// (the path went silent) from a normal closure (the peer closed) from a reset.
    /// </summary>
    public string DescribeState() => describeState?.Invoke() ?? "unknown";

    public void Dispose()
    {
        dispose?.Invoke();
        Stream.Dispose();
    }

    /// <summary>
    /// Releases the transport. Every step is bounded, so this always completes: callers release a
    /// replaced connection detached from the reconnect path.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await TryBoundedAsync(closeAsync).ConfigureAwait(false);

        if (Stream is IAsyncDisposable asyncDisposable)
        {
            await TryBoundedAsync(asyncDisposable.DisposeAsync).ConfigureAwait(false);
        }
        else
        {
            Stream.Dispose();
        }
    }

    /// <summary>Runs an operation under <see cref="CloseTimeout"/>, aborting the transport if it fails.</summary>
    private async ValueTask TryBoundedAsync(Func<ValueTask>? operation)
    {
        if (operation is null) return;
        try
        {
            await operation().AsTask().WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            dispose?.Invoke();
        }
    }
}
