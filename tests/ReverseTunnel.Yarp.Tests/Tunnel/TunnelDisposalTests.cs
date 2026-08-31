using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;
using RT = ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tests.Tunnel;

/// <summary>
/// The previous connection is released detached from the reconnect path, so its teardown must be
/// guaranteed to finish. Otherwise every reconnect over a dead path leaves a stuck task and an
/// unreleased socket behind.
/// </summary>
public class TunnelDisposalTests
{
    /// <summary>Counts transports handed out and released, with a close that never completes.</summary>
    private sealed class CountingWedgedTransport : ITunnelClientTransport
    {
        private readonly TaskCompletionSource m_closeNeverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<MultiplexingStream> CloudSides = [];
        public int Created;
        public int HardDisposed;

        public TunnelTransportKind Kind => TunnelTransportKind.WebSocket;

        public async ValueTask<TunnelTransportConnection?> ConnectAsync(TunnelClientTransportContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Created);
            (Stream edgeSide, Stream cloudSide) = FullDuplexStream.CreatePair();
            Task<MultiplexingStream> cloudTask = MultiplexingStream.CreateAsync(
                cloudSide, new MultiplexingStream.Options { ProtocolMajorVersion = 3 }, default);
            _ = cloudTask.ContinueWith(t => { lock (CloudSides) CloudSides.Add(t.Result); },
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

            await Task.Yield();
            return new TunnelTransportConnection(
                edgeSide,
                new Uri("ws://cloud.test/tunnel/test-id"),
                closeAsync: async () => await m_closeNeverCompletes.Task,
                dispose: () => Interlocked.Increment(ref HardDisposed));
        }
    }

    [Fact]
    public async Task DisposeAsync_CompletesEvenWhenTheTransportCloseNeverReturns()
    {
        CountingWedgedTransport transport = new();
        TunnelTransportConnection connection = (await transport.ConnectAsync(
            new TunnelClientTransportContext(new TunnelClientOptions(), "test-id"), default))!;

        MultiplexingStream stream = await MultiplexingStream.CreateAsync(
            connection.Stream, new MultiplexingStream.Options { ProtocolMajorVersion = 3 }, default);
        RT.TunnelClient tunnel = new(connection, stream);

        Stopwatch sw = Stopwatch.StartNew();
        await tunnel.DisposeAsync();

        // Bounded by TunnelTransportConnection.CloseTimeout rather than waiting on a peer that
        // will never answer.
        Assert.True(sw.Elapsed < TunnelTransportConnection.CloseTimeout * 3,
            $"disposal took {sw.Elapsed}, expected under {TunnelTransportConnection.CloseTimeout * 3}");

        // The graceful close gave up, so the transport was released the hard way instead of leaked.
        Assert.True(transport.HardDisposed > 0, "the transport was never hard-disposed after the close timed out");
        Assert.False(tunnel.IsConnected);
    }

    [Fact]
    public async Task RepeatedReconnects_OverADeadPath_DoNotAccumulateTransports()
    {
        CountingWedgedTransport transport = new();
        TunnelClientOptionsStore optionsStore = new(new TunnelClientOptions
        {
            TunnelId = "test-id",
            TunnelHost = "ws://cloud.test",
            IsEnabled = true
        });
        IOptions<TunnelListenerOptions> listenerOptions = Options.Create(new TunnelListenerOptions
        {
            ReconnectInterval = TimeSpan.FromMilliseconds(20),
            MaxReconnectInterval = TimeSpan.FromMilliseconds(100),
            ConnectingWatchdogTimeout = TimeSpan.FromMilliseconds(300),
            EnableReconnectBackoff = false
        });

        using TunnelClientManager manager = new(
            optionsStore, listenerOptions, transport, NullLogger<TunnelClientManager>.Instance);

        // Force several reconnects by ending each connection's read side, the way a dropped flow
        // does. Every one of them replaces a tunnel whose close will never complete.
        const int C_ROUNDS = 4;
        for (int round = 0; round < C_ROUNDS; round++)
        {
            await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));
            MultiplexingStream cloud;
            lock (transport.CloudSides) cloud = transport.CloudSides[round];
            await cloud.DisposeAsync();
            await TestWait.ForAsync(() => transport.Created >= round + 2, TimeSpan.FromSeconds(10));
        }

        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));

        // Every superseded transport was released. Only the active one may still be held, so the
        // number of hard disposals must keep pace with the number created.
        await TestWait.ForAsync(() => transport.HardDisposed >= transport.Created - 1, TimeSpan.FromSeconds(30));
        Assert.True(transport.HardDisposed >= transport.Created - 1,
            $"created {transport.Created} transports but released only {transport.HardDisposed}");
    }


}
