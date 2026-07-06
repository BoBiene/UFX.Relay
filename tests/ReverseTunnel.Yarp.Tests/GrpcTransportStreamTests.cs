using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Grpc.Protocol;
using ReverseTunnel.Yarp.Grpc.Transport;

namespace ReverseTunnel.Yarp.Tests;

public class GrpcTransportStreamTests
{
    [Fact]
    public async Task GrpcTransportStream_TransfersConnectAndByteFramesBidirectionally()
    {
        var inbound = Channel.CreateUnbounded<TunnelMessage>();
        var outbound = new List<TunnelMessage>();
        var stream = new GrpcTunnelTransportStream(
            new ChannelAsyncStreamReader(inbound.Reader),
            new CapturingAsyncStreamWriter(outbound),
            "tunnel-1",
            "connection-1");

        await stream.WriteAsync(Encoding.UTF8.GetBytes("client-to-server"), CancellationToken.None);

        Assert.Equal(2, outbound.Count);
        Assert.Equal(TunnelMessage.KindOneofCase.Connect, outbound[0].KindCase);
        Assert.Equal("tunnel-1", outbound[0].Connect.TunnelId);
        Assert.Equal("connection-1", outbound[0].Connect.ConnectionId);

        Assert.Equal(TunnelMessage.KindOneofCase.Frame, outbound[1].KindCase);
        Assert.Equal((ulong)0, outbound[1].Frame.Sequence);
        Assert.Equal("client-to-server", outbound[1].Frame.Payload.ToStringUtf8());

        await stream.WriteAsync(Encoding.UTF8.GetBytes("second-frame"), CancellationToken.None);
        Assert.Equal(3, outbound.Count);
        Assert.Equal(TunnelMessage.KindOneofCase.Frame, outbound[2].KindCase);
        Assert.Equal((ulong)1, outbound[2].Frame.Sequence);
        Assert.Equal("second-frame", outbound[2].Frame.Payload.ToStringUtf8());

        await inbound.Writer.WriteAsync(new TunnelMessage
        {
            Frame = new TunnelFrame
            {
                Payload = ByteString.CopyFromUtf8("server-to-client"),
                Sequence = 0
            }
        });
        inbound.Writer.Complete();

        var buffer = new byte[32];
        var read = await stream.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal("server-to-client", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task GrpcTransportStream_ReadAsync_WhenResponseStreamEnds_ReturnsZeroPromptly()
    {
        var inbound = Channel.CreateUnbounded<TunnelMessage>();
        inbound.Writer.Complete();
        var stream = new GrpcTunnelTransportStream(
            new ChannelAsyncStreamReader(inbound.Reader),
            new CapturingAsyncStreamWriter([]));

        var buffer = new byte[32];
        var read = await stream.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task GrpcTransportStream_AllowsMultipleMultiplexedChannelsUnderBackpressure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clientToServer = Channel.CreateUnbounded<TunnelMessage>();
        var serverToClient = Channel.CreateUnbounded<TunnelMessage>();
        await using var clientStream = new GrpcTunnelTransportStream(
            new ChannelAsyncStreamReader(serverToClient.Reader),
            new ChannelAsyncStreamWriter(clientToServer.Writer),
            "tunnel-1",
            "connection-1");
        await using var serverStream = new GrpcTunnelTransportStream(
            new ChannelAsyncStreamReader(clientToServer.Reader),
            new ChannelAsyncStreamWriter(serverToClient.Writer));

        var options = new MultiplexingStream.Options { ProtocolMajorVersion = 3 };
        var clientMultiplexingTask = MultiplexingStream.CreateAsync(clientStream, options, timeout.Token);
        var serverMultiplexingTask = MultiplexingStream.CreateAsync(serverStream, options, timeout.Token);
        await using var clientMultiplexing = await clientMultiplexingTask.WaitAsync(timeout.Token);
        await using var serverMultiplexing = await serverMultiplexingTask.WaitAsync(timeout.Token);

        var slowServerTask = AcceptNextChannelAsync(serverMultiplexing, timeout.Token);
        var slowClient = await clientMultiplexing.OfferChannelAsync("slow", timeout.Token);
        var slowServer = await slowServerTask.WaitAsync(timeout.Token);
        var fastServerTask = AcceptNextChannelAsync(serverMultiplexing, timeout.Token);
        var fastClient = await clientMultiplexing.OfferChannelAsync("fast", timeout.Token);
        var fastServer = await fastServerTask.WaitAsync(timeout.Token);

        var slowWriter = Task.Run(async () =>
        {
            var payload = new byte[64 * 1024];
            for (var i = 0; i < 128; i++)
            {
                await slowClient.Output.WriteAsync(payload, timeout.Token);
            }
        }, timeout.Token);

        for (var i = 0; i < 20; i++)
        {
            var expected = $"ping-{i}";
            await fastClient.Output.WriteAsync(Encoding.UTF8.GetBytes(expected), timeout.Token);

            var received = await ReadUtf8Async(fastServer.Input, timeout.Token);

            Assert.Contains(expected, received, StringComparison.Ordinal);
        }

        await timeout.CancelAsync();
        slowClient.Dispose();
        slowServer.Dispose();
        fastClient.Dispose();
        fastServer.Dispose();
        try
        {
            await slowWriter.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
        }
    }

    private static Task<MultiplexingStream.Channel> AcceptNextChannelAsync(
        MultiplexingStream stream,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<MultiplexingStream.Channel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        EventHandler<MultiplexingStream.ChannelOfferEventArgs>? handler = null;
        handler = (_, args) =>
        {
            stream.ChannelOffered -= handler;
            registration.Dispose();
            _ = AcceptOfferedChannelAsync(stream, args.Name, completion);
        };
        registration = cancellationToken.Register(() =>
        {
            stream.ChannelOffered -= handler;
            completion.TrySetCanceled(cancellationToken);
        });
        stream.ChannelOffered += handler;
        return completion.Task;
    }

    private static async Task AcceptOfferedChannelAsync(
        MultiplexingStream stream,
        string channelName,
        TaskCompletionSource<MultiplexingStream.Channel> completion)
    {
        try
        {
            completion.TrySetResult(await stream.AcceptChannelAsync(channelName).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }
    private static async Task<string> ReadUtf8Async(PipeReader reader, CancellationToken cancellationToken)
    {
        var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Encoding.UTF8.GetString(result.Buffer.ToArray());
        }
        finally
        {
            reader.AdvanceTo(result.Buffer.End);
        }
    }

    private sealed class ChannelAsyncStreamReader(ChannelReader<TunnelMessage> reader) : IAsyncStreamReader<TunnelMessage>
    {
        public TunnelMessage Current { get; private set; } = new();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return reader.TryRead(out var message) && (Current = message) is not null;
            }

            return false;
        }
    }

    private sealed class CapturingAsyncStreamWriter(List<TunnelMessage> messages) : IAsyncStreamWriter<TunnelMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(TunnelMessage message)
        {
            messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ChannelAsyncStreamWriter(ChannelWriter<TunnelMessage> writer) : IAsyncStreamWriter<TunnelMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(TunnelMessage message) => writer.WriteAsync(message).AsTask();
    }
}