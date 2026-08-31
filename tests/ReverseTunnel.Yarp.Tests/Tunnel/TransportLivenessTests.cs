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
/// A WebSocket that aborts on its keep-alive timeout does not complete the multiplexing stream: the
/// stream wrapper surfaces neither an exception nor EOF to the read loop, so
/// <c>MultiplexingStream.Completion</c> stays pending. Liveness is therefore asked of the transport.
/// </summary>
public class TransportLivenessTests
{
    /// <summary>A transport whose liveness can be switched off, as an aborted socket does.</summary>
    private sealed class SwitchableTransport : ITunnelClientTransport
    {
        private volatile bool m_alive = true;
        public readonly List<MultiplexingStream> CloudSides = [];
        public int ConnectCount;

        public TunnelTransportKind Kind => TunnelTransportKind.WebSocket;

        /// <summary>Models the socket aborting: nothing else about the streams changes.</summary>
        public void KillTransport() => m_alive = false;

        public async ValueTask<TunnelTransportConnection?> ConnectAsync(TunnelClientTransportContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ConnectCount);
            bool aliveForThisConnection = true;
            (Stream edgeSide, Stream cloudSide) = FullDuplexStream.CreatePair();
            Task<MultiplexingStream> cloudTask = MultiplexingStream.CreateAsync(
                cloudSide, new MultiplexingStream.Options { ProtocolMajorVersion = 3 }, default);
            _ = cloudTask.ContinueWith(t => { lock (CloudSides) CloudSides.Add(t.Result); },
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

            await Task.Yield();
            return new TunnelTransportConnection(
                edgeSide,
                new Uri("ws://cloud.test/tunnel/test-id"),
                closeAsync: () => ValueTask.CompletedTask,
                dispose: () => { },
                // Only the FIRST connection is killable, so the reconnect comes up healthy.
                isAlive: () => aliveForThisConnection && (m_alive || Interlocked.CompareExchange(ref ConnectCount, 0, 0) > 1));
        }
    }

    [Fact]
    public async Task DeadTransport_MakesTunnelNotConnected_EvenThoughTheStreamNeverCompletes()
    {
        (Stream edgeSide, Stream cloudSide) = FullDuplexStream.CreatePair();
        MultiplexingStream.Options options = new() { ProtocolMajorVersion = 3 };
        Task<MultiplexingStream> cloudTask = MultiplexingStream.CreateAsync(cloudSide, options, default);
        Task<MultiplexingStream> edgeTask = MultiplexingStream.CreateAsync(edgeSide, options, default);
        MultiplexingStream cloudMx = await cloudTask;
        MultiplexingStream edgeMx = await edgeTask;

        bool alive = true;
        TunnelTransportConnection connection = new(
            edgeSide,
            new Uri("ws://cloud.test/tunnel/test-id"),
            closeAsync: () => ValueTask.CompletedTask,
            dispose: () => { },
            isAlive: () => alive);

        RT.TunnelClient tunnel = new(connection, edgeMx);
        Assert.True(tunnel.IsConnected);

        // The socket aborts. The multiplexing stream is deliberately left untouched, exactly as the
        // real runtime leaves it.
        alive = false;

        Assert.False(tunnel.Completion.IsCompleted);   // the old signal still says nothing is wrong
        Assert.False(tunnel.IsConnected);              // the transport says otherwise, and wins

        await tunnel.DisposeAsync();
        await cloudMx.DisposeAsync();
    }

    [Fact]
    public async Task DeadTransport_MakesTheClientManagerReconnect()
    {
        SwitchableTransport transport = new();
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

        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));
        Assert.Equal(1, transport.ConnectCount);
        RT.TunnelClient? first = manager.Tunnel;

        // Abort the socket without touching the streams. Nothing else signals the failure, so the
        // reconnect can only happen if the manager reconciles Connected against the transport.
        transport.KillTransport();

        await TestWait.ForAsync(() => transport.ConnectCount >= 2, TimeSpan.FromSeconds(15));
        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(15));

        Assert.NotSame(first, manager.Tunnel);
        Assert.True(manager.Tunnel!.IsConnected);
    }


}
