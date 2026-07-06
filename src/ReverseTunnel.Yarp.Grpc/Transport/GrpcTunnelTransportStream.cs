using Google.Protobuf;
using Grpc.Core;
using ReverseTunnel.Yarp.Grpc.Protocol;

namespace ReverseTunnel.Yarp.Grpc.Transport;

public sealed class GrpcTunnelTransportStream(
    IAsyncStreamReader<TunnelMessage> reader,
    IAsyncStreamWriter<TunnelMessage> writer,
    string? tunnelId = null,
    string? connectionId = null,
    Func<ValueTask>? completeWritesAsync = null) : Stream
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private byte[]? currentPayload;
    private int currentOffset;
    private ulong writeSequence;
    private bool completed;
    private bool connectWritten;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (completed)
        {
            return 0;
        }

        while (currentPayload is null || currentOffset >= currentPayload.Length)
        {
            if (!await reader.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                completed = true;
                return 0;
            }

            if (reader.Current.KindCase != TunnelMessage.KindOneofCase.Frame)
            {
                continue;
            }

            currentPayload = reader.Current.Frame.Payload.ToByteArray();
            currentOffset = 0;
            if (currentPayload.Length == 0)
            {
                continue;
            }
        }

        var available = currentPayload.Length - currentOffset;
        var copy = Math.Min(available, buffer.Length);
        currentPayload.AsMemory(currentOffset, copy).CopyTo(buffer);
        currentOffset += copy;
        return copy;
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public async ValueTask WriteConnectAsync(CancellationToken cancellationToken = default)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteConnectIfNeededAsync().ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteConnectIfNeededAsync().ConfigureAwait(false);

            var frame = new TunnelFrame
            {
                Payload = ByteString.CopyFrom(buffer.Span),
                Sequence = writeSequence++
            };

            await writer.WriteAsync(new TunnelMessage { Frame = frame }).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            writeLock.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (completeWritesAsync is not null)
        {
            await completeWritesAsync().ConfigureAwait(false);
        }

        writeLock.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask WriteConnectIfNeededAsync()
    {
        if (connectWritten)
        {
            return;
        }

        connectWritten = true;
        if (string.IsNullOrWhiteSpace(tunnelId))
        {
            return;
        }

        await writer.WriteAsync(new TunnelMessage
        {
            Connect = new TunnelConnect
            {
                TunnelId = tunnelId,
                ConnectionId = connectionId ?? string.Empty
            }
        }).ConfigureAwait(false);
    }
}
