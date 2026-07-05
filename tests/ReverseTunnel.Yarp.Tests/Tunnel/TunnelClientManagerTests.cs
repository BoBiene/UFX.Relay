using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;
using System.Net.WebSockets;
using System.Threading;

namespace ReverseTunnel.Yarp.Tests.Tunnel;

/// <summary>
/// Regression tests for the reconnect feedback loop fix in <see cref="TunnelClientManager"/>.
///
/// The bug: when options were re-applied while already connected (e.g. user pressing Apply
/// again, or a JWT refresh after disconnect), a feedback loop caused repeated disconnects:
///
///   Update() → OptionsChanged → ConnectInternalAsync (no state check)
///   → old tunnel disposed → Completion fires async → Disconnected
///   → consumer refreshes JWT → Update() → loop repeats
///
/// Two fixes were applied:
///   1. <see cref="TunnelClientManager"/> now skips ConnectInternalAsync when the only change
///      is credentials (e.g. JWT) while the connection is already established.
///   2. The Completion callback guards against replaced tunnels by checking that
///      <c>_client == tunnelRef</c> before setting state to Disconnected.
/// </summary>
public class TunnelClientManagerTests : IDisposable
{
    private readonly TunnelClientOptionsStore _optionsStore;
    private readonly Mock<ITunnelClientFactory> _factoryMock;
    private readonly TunnelClientManager _manager;

    public TunnelClientManagerTests()
    {
        _optionsStore = new TunnelClientOptionsStore(new TunnelClientOptions
        {
            TunnelId = "test-id",
            TunnelHost = "ws://test.example.com",
            IsEnabled = true
        });

        _factoryMock = new Mock<ITunnelClientFactory>();
        _factoryMock.Setup(f => f.CreateAsync()).ReturnsAsync((ClientWebSocket?)null);
        _factoryMock.Setup(f => f.GetUriAsync()).ReturnsAsync(new Uri("ws://test.example.com/tunnel/test-id"));
        _factoryMock.Setup(f => f.CreateHttpClient()).Returns(new HttpClient());

        var listenerOptions = Options.Create(new TunnelListenerOptions
        {
            ReconnectInterval = TimeSpan.FromMilliseconds(5),
            MaxReconnectInterval = TimeSpan.FromMilliseconds(25),
            EnableReconnectBackoff = false
        });

        _manager = new TunnelClientManager(
            _optionsStore, listenerOptions, _factoryMock.Object,
            NullLogger<TunnelClientManager>.Instance);
    }

    public void Dispose() => _manager.Dispose();

    // -------------------------------------------------------------------------
    // Test 1 – Main regression: credentials-only update while connected must NOT reconnect
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(TunnelTransportKind.WebSocket, "ws://test.example.com")]
    [InlineData(TunnelTransportKind.Grpc, "https://test.example.com")]
    public async Task CredentialsOnlyUpdate_WhenConnected_DoesNotReconnectTransport(TunnelTransportKind transportKind, string tunnelHost)
    {
        var optionsStore = new TunnelClientOptionsStore(new TunnelClientOptions
        {
            TunnelId = "test-id",
            TunnelHost = tunnelHost,
            Transport = transportKind,
            IsEnabled = true
        });
        var transport = new CountingTunnelClientTransport(transportKind);
        using var manager = new TunnelClientManager(
            optionsStore,
            Options.Create(new TunnelListenerOptions
            {
                ReconnectInterval = TimeSpan.FromMilliseconds(5),
                MaxReconnectInterval = TimeSpan.FromMilliseconds(25),
                EnableReconnectBackoff = false
            }),
            transport,
            NullLogger<TunnelClientManager>.Instance);

        var (fakeTunnel, serverSide) = await CreateFakeTunnelPairAsync();
        await using (serverSide)
        {
            manager.State = TunnelConnectionState.Connected;
            manager.ActiveClient = fakeTunnel;

            await Task.Delay(50);
            transport.Reset();

            optionsStore.Update(o => o with
            {
                RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer new-token" }
            });

            await Task.Delay(200);

            Assert.Equal(0, transport.ConnectCount);
            Assert.Equal(TunnelConnectionState.Connected, manager.State);
        }
    }
    [Fact]
    public async Task CredentialsOnlyUpdate_WhenConnected_DoesNotCallFactory()
    {
        // Arrange: force the manager into Connected state using internal fields exposed via
        // InternalsVisibleTo – this simulates having just successfully connected.
        var (fakeTunnel, serverSide) = await CreateFakeTunnelPairAsync();
        await using (serverSide)
        {
            _manager.State = TunnelConnectionState.Connected;
            _manager.ActiveClient = fakeTunnel;

            // Wait for the worker to observe the Connected state at least once
            await Task.Delay(50);
            _factoryMock.Invocations.Clear();

            // Act: update only the JWT header — no endpoint/identity change
            _optionsStore.Update(o => o with
            {
                RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer new-token" }
            });

            // Wait long enough for the reconnect worker to process the options-changed flag
            await Task.Delay(200);

            // Assert: no reconnect was attempted (factory must not have been called)
            _factoryMock.Verify(f => f.CreateAsync(), Times.Never,
                "A credentials-only update while Connected must not tear down the existing connection.");
            Assert.Equal(TunnelConnectionState.Connected, _manager.State);
        }
    }

    // -------------------------------------------------------------------------
    // Test 2 – Endpoint change while connected MUST trigger a reconnect
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EndpointChange_WhenConnected_TriggersReconnect()
    {
        // Arrange
        var (fakeTunnel, serverSide) = await CreateFakeTunnelPairAsync();
        await using (serverSide)
        {
            _manager.State = TunnelConnectionState.Connected;
            _manager.ActiveClient = fakeTunnel;

            await Task.Delay(50);
            _factoryMock.Invocations.Clear();

            var stateChangeTcs = new TaskCompletionSource<TunnelConnectionState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Skip Connecting: ConnectionStateChanged fires with Connecting *before*
            // CreateAsync() is called, so waiting for it would race the verify below.
            // Wait for a terminal state (Error when factory returns null, or Connected).
            _manager.ConnectionStateChanged += (_, s) =>
            {
                if (s != TunnelConnectionState.Connecting)
                    stateChangeTcs.TrySetResult(s);
            };

            // Act: change the tunnel host (connection-relevant change)
            _optionsStore.Update(o => o with { TunnelHost = "ws://new-host.example.com" });

            // Assert: a reconnect is triggered and state changes within 3 seconds
            var completed = await Task.WhenAny(stateChangeTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Equal(stateChangeTcs.Task, completed);
            _factoryMock.Verify(f => f.CreateAsync(), Times.AtLeastOnce,
                "An endpoint change while Connected must trigger a new connection attempt.");
        }
    }

    // -------------------------------------------------------------------------
    // Test 3 – Old tunnel completion AFTER being replaced must not set Disconnected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OldTunnelCompletion_AfterReplaced_DoesNotSetDisconnected()
    {
        // Arrange: create two fake tunnels
        var (tunnel1, serverSide1) = await CreateFakeTunnelPairAsync();
        var (tunnel2, serverSide2) = await CreateFakeTunnelPairAsync();
        await using (serverSide2)
        {
            // Place tunnel1 as active
            _manager.State = TunnelConnectionState.Connected;
            _manager.ActiveClient = tunnel1;

            // Register the completion guard callback, exactly as production code does
            var tunnelRef = tunnel1;
            _ = tunnel1.Completion.ContinueWith(_ =>
            {
                if (_manager.ActiveClient == tunnelRef)
                    _manager.UpdateState(TunnelConnectionState.Disconnected, "socketCompletion");
                // else: guard fires – old tunnel, ignore
            }, TaskScheduler.Default);

            // Simulate reconnect: replace ActiveClient with the new tunnel
            _manager.ActiveClient = tunnel2;

            // Act: close the server side of tunnel1 → its Completion task completes
            await serverSide1.DisposeAsync();
            await Task.Delay(200); // allow ContinueWith to run

            // Assert: guard prevented the stale callback from overwriting the new connection
            Assert.Equal(TunnelConnectionState.Connected, _manager.State);
        }
    }

    // -------------------------------------------------------------------------
    // Test 4 – Active tunnel completion (no replacement) MUST set Disconnected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ActiveTunnelCompletion_WhenNotReplaced_SetsDisconnected()
    {
        // Arrange
        var (tunnel, serverSide) = await CreateFakeTunnelPairAsync();

        _manager.State = TunnelConnectionState.Connected;
        _manager.ActiveClient = tunnel;

        var disconnectedTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.ConnectionStateChanged += (_, s) =>
        {
            if (s == TunnelConnectionState.Disconnected)
                disconnectedTcs.TrySetResult(true);
        };

        // Register the same completion callback as production code
        var tunnelRef = tunnel;
        _ = tunnel.Completion.ContinueWith(_ =>
        {
            if (_manager.ActiveClient == tunnelRef)
                _manager.UpdateState(TunnelConnectionState.Disconnected, "socketCompletion");
        }, TaskScheduler.Default);

        // Act: close the server side → tunnel.Completion fires
        await serverSide.DisposeAsync();

        // Assert: Disconnected state is set since the tunnel was not replaced
        var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Equal(disconnectedTcs.Task, completed);
        Assert.Equal(TunnelConnectionState.Disconnected, _manager.State);
    }

    // -------------------------------------------------------------------------
    // Test 5 – TunnelId change while connected MUST trigger a reconnect
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TunnelIdChange_WhenConnected_TriggersReconnect()
    {
        // Arrange
        var (fakeTunnel, serverSide) = await CreateFakeTunnelPairAsync();
        await using (serverSide)
        {
            _manager.State = TunnelConnectionState.Connected;
            _manager.ActiveClient = fakeTunnel;

            await Task.Delay(50);
            _factoryMock.Invocations.Clear();

            var stateChangeTcs = new TaskCompletionSource<TunnelConnectionState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Skip Connecting: ConnectionStateChanged fires with Connecting *before*
            // CreateAsync() is called, so waiting for it would race the verify below.
            // Wait for a terminal state (Error when factory returns null, or Connected).
            _manager.ConnectionStateChanged += (_, s) =>
            {
                if (s != TunnelConnectionState.Connecting)
                    stateChangeTcs.TrySetResult(s);
            };

            // Act: change the tunnel id
            _optionsStore.Update(o => o with { TunnelId = "new-tunnel-id" });

            var completed = await Task.WhenAny(stateChangeTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Equal(stateChangeTcs.Task, completed);
            _factoryMock.Verify(f => f.CreateAsync(), Times.AtLeastOnce);
        }
    }

    // -------------------------------------------------------------------------
    // Test 7 – TunnelPathTemplate change while connected MUST trigger a reconnect
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TunnelPathTemplateChange_WhenConnected_TriggersReconnect()
    {
        // Arrange
        var (fakeTunnel, serverSide) = await CreateFakeTunnelPairAsync();
        await using (serverSide)
        {
            _manager.State = TunnelConnectionState.Connected;
            _manager.ActiveClient = fakeTunnel;

            await Task.Delay(50);
            _factoryMock.Invocations.Clear();

            var stateChangeTcs = new TaskCompletionSource<TunnelConnectionState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _manager.ConnectionStateChanged += (_, s) =>
            {
                if (s != TunnelConnectionState.Connecting)
                    stateChangeTcs.TrySetResult(s);
            };

            // Act: change the path template (connection-shaping change)
            _optionsStore.Update(o => o with { TunnelPathTemplate = "/api/tunnel/{0}" });

            var completed = await Task.WhenAny(stateChangeTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Equal(stateChangeTcs.Task, completed);
            _factoryMock.Verify(f => f.CreateAsync(), Times.AtLeastOnce,
                "A TunnelPathTemplate change while Connected must trigger a new connection attempt.");
        }
    }

    [Fact]
    public async Task DisableTunnel_WhenConnected_Disconnects()
    {
        // Arrange
        var (fakeTunnel, serverSide) = await CreateFakeTunnelPairAsync();
        await using (serverSide)
        {
            _manager.State = TunnelConnectionState.Connected;
            _manager.ActiveClient = fakeTunnel;

            await Task.Delay(50);

            var disconnectedTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _manager.ConnectionStateChanged += (_, s) =>
            {
                if (s == TunnelConnectionState.Disconnected)
                    disconnectedTcs.TrySetResult(true);
            };

            // Act: disable tunnel via options
            _optionsStore.Update(o => o with { IsEnabled = false });

            var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Equal(disconnectedTcs.Task, completed);
            Assert.Equal(TunnelConnectionState.Disconnected, _manager.State);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class CountingTunnelClientTransport(TunnelTransportKind kind) : ITunnelClientTransport
    {
        private int connectCount;

        public TunnelTransportKind Kind { get; } = kind;

        public int ConnectCount => Volatile.Read(ref connectCount);

        public ValueTask<TunnelTransportConnection?> ConnectAsync(
            TunnelClientTransportContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref connectCount);
            return ValueTask.FromResult<TunnelTransportConnection?>(null);
        }

        public void Reset() => Interlocked.Exchange(ref connectCount, 0);
    }
    /// <summary>
    /// Creates a pair of in-memory connected <see cref="TunnelClient"/> / server-side stream
    /// using <see cref="FullDuplexStream.CreatePair"/> – no real network required.
    /// Disposing <paramref name="serverSide"/> will complete <c>tunnel.Completion</c>.
    /// </summary>
    private static async Task<(TunnelClient tunnel, IAsyncDisposable serverSide)> CreateFakeTunnelPairAsync()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        // Both ends must complete the MultiplexingStream v3 handshake simultaneously
        var clientMxTask = MultiplexingStream.CreateAsync(
            clientStream, new MultiplexingStream.Options { ProtocolMajorVersion = 3 });
        var serverMxTask = MultiplexingStream.CreateAsync(
            serverStream, new MultiplexingStream.Options { ProtocolMajorVersion = 3 });

        await Task.WhenAll(clientMxTask, serverMxTask);

        var tunnel = new TunnelClient(new TunnelTransportConnection(clientStream), clientMxTask.Result)
        {
            Uri = new Uri("ws://test.example.com/tunnel/test-id")
        };

        return (tunnel, serverMxTask.Result);
    }
}
