using System.Diagnostics;
using Nerdbank.Streams;
using RT = ReverseTunnel.Yarp.Tunnel;

namespace ReverseTunnel.Yarp.Tests.Tunnel;

/// <summary>
/// When a transport stops carrying data without closing, the multiplexing stream's Completion task
/// stays pending, so a tunnel cannot tell from it whether it is still usable. These tests cover
/// what does report it: a bounded channel offer, invalidation, and disposal.
/// </summary>
public class TunnelStateTests
{
    /// <summary>Duplex stream that can be frozen: no progress, no close, no exception.</summary>
    private sealed class FreezableStream(Stream inner) : Stream
    {
        private volatile bool m_frozen;
        public void Freeze() => m_frozen = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (m_frozen) await Task.Delay(Timeout.Infinite, ct);
            int read = await inner.ReadAsync(buffer, ct);
            if (m_frozen) await Task.Delay(Timeout.Infinite, ct);
            return read;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (m_frozen) { await Task.Delay(Timeout.Infinite, ct); return; }
            await inner.WriteAsync(buffer, ct);
        }

        public override Task FlushAsync(CancellationToken ct) => m_frozen ? Task.CompletedTask : inner.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private static async Task<(RT.Tunnel Host, RT.Tunnel Edge, FreezableStream HostLink, FreezableStream EdgeLink)> CreateTunnelPairAsync()
    {
        (Stream hostRaw, Stream edgeRaw) = FullDuplexStream.CreatePair();
        FreezableStream hostLink = new(hostRaw);
        FreezableStream edgeLink = new(edgeRaw);
        MultiplexingStream.Options options = new() { ProtocolMajorVersion = 3 };

        Task<MultiplexingStream> hostTask = MultiplexingStream.CreateAsync(hostLink, options, default);
        Task<MultiplexingStream> edgeTask = MultiplexingStream.CreateAsync(edgeLink, options, default);

        RT.Tunnel host = new(await hostTask) { Uri = new Uri("wss://localhost/tunnel/test") };
        RT.Tunnel edge = new(await edgeTask);
        return (host, edge, hostLink, edgeLink);
    }

    /// <summary>Drives Tunnel.GetChannelAsync(null, ...) the way TunnelConnectionListener does.</summary>
    private static Task RunEdgeAcceptLoopAsync(RT.Tunnel edge, CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await edge.GetChannelAsync(null, ct); }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(50, ct); }
        }
    }, ct);

    [Fact]
    public async Task FrozenTransport_ChannelOfferFailsWithOfferTimeout()
    {
        (RT.Tunnel host, RT.Tunnel edge, FreezableStream hostLink, FreezableStream edgeLink) = await CreateTunnelPairAsync();
        using CancellationTokenSource loopCts = new();
        Task acceptLoop = RunEdgeAcceptLoopAsync(edge, loopCts.Token);

        using (CancellationTokenSource ok = new(TimeSpan.FromSeconds(10)))
        {
            Assert.NotNull(await host.GetChannelAsync("0HNO1ERDTAARG:00000001", ok.Token));
        }

        edgeLink.Freeze();
        hostLink.Freeze();
        await Task.Delay(300);

        Stopwatch sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<RT.TunnelChannelOfferTimeoutException>(
            () => host.GetChannelAsync("0HNO1ERDTAARG:00000008", TimeSpan.FromSeconds(2)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"offer took {sw.Elapsed}");

        await loopCts.CancelAsync();
        await acceptLoop;
    }

    [Fact]
    public async Task InvalidatedTunnel_IsNotConnected()
    {
        (RT.Tunnel host, _, _, _) = await CreateTunnelPairAsync();
        Assert.True(host.IsConnected);

        host.Invalidate("channel offer timed out");

        Assert.False(host.IsConnected);
    }

    [Fact]
    public async Task DisposedTunnel_IsNotConnected()
    {
        (RT.Tunnel host, _, _, _) = await CreateTunnelPairAsync();
        Assert.True(host.IsConnected);

        host.Dispose();

        Assert.False(host.IsConnected);
    }

    [Fact]
    public async Task HealthyTunnel_ServesRepeatedChannelOffers()
    {
        (RT.Tunnel host, RT.Tunnel edge, _, _) = await CreateTunnelPairAsync();
        using CancellationTokenSource loopCts = new();
        Task acceptLoop = RunEdgeAcceptLoopAsync(edge, loopCts.Token);

        for (int i = 0; i < 5; i++)
        {
            using CancellationTokenSource ok = new(TimeSpan.FromSeconds(10));
            Assert.NotNull(await host.GetChannelAsync($"0HNO1ERDTAARG:{i:X8}", TimeSpan.FromSeconds(10), ok.Token));
        }

        await loopCts.CancelAsync();
        await acceptLoop;
    }
}
