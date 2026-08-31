using Microsoft.AspNetCore.Connections;
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
/// The client publishes a new connection without waiting for the previous one to be torn down, so a
/// close that cannot complete does not hold up the transition to Connected. A watchdog and a
/// reconciliation pass correct a connection state that no longer matches the tunnel.
/// </summary>
public class TunnelReconnectTests
{
    /// <summary>A transport whose close callback never completes.</summary>
    private sealed class BlockingCloseTransport : ITunnelClientTransport
    {
        private readonly TaskCompletionSource m_neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<MultiplexingStream> CloudSides = [];
        public int ConnectCount;

        public TunnelTransportKind Kind => TunnelTransportKind.WebSocket;

        public async ValueTask<TunnelTransportConnection?> ConnectAsync(TunnelClientTransportContext context, CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref ConnectCount);
            (Stream edgeSide, Stream cloudSide) = FullDuplexStream.CreatePair();

            // The host accepts the WebSocket and registers the TunnelHost as soon as the
            // handshake completes - it never waits for the client's state machine.
            Task<MultiplexingStream> cloudTask = MultiplexingStream.CreateAsync(
                cloudSide, new MultiplexingStream.Options { ProtocolMajorVersion = 3 }, default);
            _ = cloudTask.ContinueWith(t => { lock (CloudSides) CloudSides.Add(t.Result); },
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

            await Task.Yield();
            return new TunnelTransportConnection(
                edgeSide,
                new Uri($"ws://cloud.test/tunnel/test-id#{index}"),
                closeAsync: async () => await m_neverCompletes.Task,
                dispose: () => { });
        }
    }

    private sealed class FixedTunnelIdProvider(string id) : ITunnelIdProvider
    {
        public ValueTask<string?> GetTunnelIdAsync() => new(id);
    }

    private static IOptions<TunnelListenerOptions> FastListenerOptions() => Options.Create(new TunnelListenerOptions
    {
        ReconnectInterval = TimeSpan.FromMilliseconds(20),
        MaxReconnectInterval = TimeSpan.FromMilliseconds(100),
        DelayWhenDisconnected = TimeSpan.FromMilliseconds(50),
        DelayWhenDisabled = TimeSpan.FromMilliseconds(50),
        ConnectingWatchdogTimeout = TimeSpan.FromMilliseconds(300),
        EnableReconnectBackoff = false
    });

    private static TunnelClientOptionsStore EnabledOptions() => new(new TunnelClientOptions
    {
        TunnelId = "test-id",
        TunnelHost = "ws://cloud.test",
        IsEnabled = true
    });

    private static TunnelConnectionListener CreateListener(
        TunnelClientManager manager, IOptions<TunnelListenerOptions> listenerOptions) => new(
            new TunnelEndpoint(),
            new FixedTunnelIdProvider("test-id"),
            manager,
            listenerOptions,
            NullLogger<TunnelConnectionListener>.Instance);

    [Fact]
    public async Task Reconnect_WhenClosingThePreviousSocketNeverCompletes_StillReachesConnected()
    {
        BlockingCloseTransport transport = new();
        IOptions<TunnelListenerOptions> listenerOptions = FastListenerOptions();
        using TunnelClientManager manager = new(
            EnabledOptions(), listenerOptions, transport, NullLogger<TunnelClientManager>.Instance);

        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));
        RT.TunnelClient? first = manager.Tunnel;
        Assert.NotNull(first);
        Assert.Equal(1, transport.ConnectCount);

        // End the first connection's read side, forcing a reconnect whose teardown cannot finish.
        MultiplexingStream firstCloud;
        lock (transport.CloudSides) firstCloud = transport.CloudSides[0];
        await firstCloud.DisposeAsync();

        await TestWait.ForAsync(() => transport.ConnectCount >= 2, TimeSpan.FromSeconds(10));

        // Availability of the new connection does not depend on the teardown of the dead one.
        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));

        RT.TunnelClient? second = manager.Tunnel;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(second.IsConnected);
    }

    [Fact]
    public async Task Reconnect_WhenClosingThePreviousSocketNeverCompletes_TunnelListenerKeepsServing()
    {
        BlockingCloseTransport transport = new();
        IOptions<TunnelListenerOptions> listenerOptions = FastListenerOptions();
        using TunnelClientManager manager = new(
            EnabledOptions(), listenerOptions, transport, NullLogger<TunnelClientManager>.Instance);

        TunnelConnectionListener listener = CreateListener(manager, listenerOptions);
        await listener.BindAsync();
        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));

        MultiplexingStream firstCloud;
        lock (transport.CloudSides) firstCloud = transport.CloudSides[0];
        await firstCloud.DisposeAsync();

        await TestWait.ForAsync(() => transport.ConnectCount >= 2, TimeSpan.FromSeconds(10));
        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));

        MultiplexingStream secondCloud;
        lock (transport.CloudSides) secondCloud = transport.CloudSides[1];
        RT.Tunnel cloudTunnel = new(secondCloud);

        // Kestrel asks the tunnel endpoint for a connection while a request offers a channel.
        using CancellationTokenSource acceptCts = new(TimeSpan.FromSeconds(15));
        Task<ConnectionContext?> acceptTask = listener.AcceptAsync(acceptCts.Token).AsTask();

        using CancellationTokenSource offerCts = new(TimeSpan.FromSeconds(15));
        Assert.NotNull(await cloudTunnel.GetChannelAsync("0HNO1ERDTAARG:00000008", offerCts.Token));

        ConnectionContext? connection = await acceptTask;
        Assert.NotNull(connection);
        Assert.Equal("test-id", connection.Features.Get<ITunnelRequestFeature>()!.TunnelId);
    }

    [Fact]
    public async Task StaleConnected_IsReconciledAgainstTheActualTunnel()
    {
        BlockingCloseTransport transport = new();
        IOptions<TunnelListenerOptions> listenerOptions = FastListenerOptions();
        using TunnelClientManager manager = new(
            EnabledOptions(), listenerOptions, transport, NullLogger<TunnelClientManager>.Instance);

        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));
        RT.TunnelClient tunnel = manager.Tunnel!;

        // Observed through the event, because the correction is followed immediately by a reconnect.
        List<TunnelConnectionState> observed = [];
        manager.ConnectionStateChanged += (_, state) => { lock (observed) observed.Add(state); };

        // Connected while the tunnel is not, as a missed Completion continuation leaves it.
        tunnel.Invalidate("test");
        Assert.Equal(TunnelConnectionState.Connected, manager.ConnectionState);
        Assert.False(tunnel.IsConnected);

        await TestWait.ForAsync(
            () => { lock (observed) return observed.Contains(TunnelConnectionState.Disconnected); },
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConnectingThatStopsProgressing_IsForcedBackToDisconnectedByTheWatchdog()
    {
        BlockingCloseTransport transport = new();
        IOptions<TunnelListenerOptions> listenerOptions = FastListenerOptions();
        using TunnelClientManager manager = new(
            EnabledOptions(), listenerOptions, transport, NullLogger<TunnelClientManager>.Instance);

        await TestWait.ForAsync(() => manager.ConnectionState == TunnelConnectionState.Connected, TimeSpan.FromSeconds(10));

        List<TunnelConnectionState> observed = [];
        manager.ConnectionStateChanged += (_, state) => { lock (observed) observed.Add(state); };

        // Connecting with nobody driving it, as the cancellation catch in ConnectInternalAsync leaves it.
        manager.UpdateState(TunnelConnectionState.Connecting, "test");
        Assert.Equal(TunnelConnectionState.Connecting, manager.ConnectionState);

        await TestWait.ForAsync(
            () => { lock (observed) return observed.Contains(TunnelConnectionState.Disconnected); },
            TimeSpan.FromSeconds(10));
    }
}
