using System.Diagnostics;
using System.Net.WebSockets;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Grpc.Protocol;
using ReverseTunnel.Yarp.Grpc.Transport;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;
using ReverseTunnel.Yarp.Tunnel.Registry;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tests;

public class GrpcMetadataAndHostingTests
{
    [Fact]
    public void GrpcMetadataValidator_NormalizesHeadersAndSupportsBinaryMetadata()
    {
        var binaryValue = new byte[] { 1, 2, 3 };

        var metadata = GrpcMetadataValidator.CreateRequestMetadata(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer jwt",
            ["X-Tunnel-Key"] = "client-key",
            ["X-Route-Id"] = "route-42",
            ["trace-bin"] = Convert.ToBase64String(binaryValue)
        });

        Assert.Equal("Bearer jwt", Assert.Single(metadata, entry => entry.Key == "authorization").Value);
        Assert.Equal("client-key", Assert.Single(metadata, entry => entry.Key == "x-tunnel-key").Value);
        Assert.Equal("route-42", Assert.Single(metadata, entry => entry.Key == "x-route-id").Value);
        Assert.Equal(binaryValue, Assert.Single(metadata, entry => entry.Key == "trace-bin").ValueBytes);
    }

    [Theory]
    [InlineData(":path", "/tunnel")]
    [InlineData("grpc-timeout", "10S")]
    [InlineData("Invalid Header", "route-42")]
    [InlineData("\u00fcmlaut", "value")]
    [InlineData("authorization", "Bearer \u00e4")]
    [InlineData("authorization", "Bearer\r\nvalue")]
    [InlineData("trace-bin", "not-base64")]
    public void GrpcMetadataValidator_InvalidCallerHeadersFailFast(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GrpcMetadataValidator.CreateRequestMetadata(new Dictionary<string, string>
            {
                [key] = value
            }));

        Assert.Contains("gRPC metadata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcTunnelClientTransport_InvalidRequestHeaderFailsBeforeOpeningCall()
    {
        var transport = new GrpcTunnelClientTransport(Options.Create(new ReverseTunnelGrpcTransportOptions()));
        var options = new TunnelClientOptions
        {
            TunnelId = "tunnel-1",
            TunnelHost = "https://relay.example.com",
            Transport = TunnelTransportKind.Grpc,
            RequestHeaders = new Dictionary<string, string>
            {
                [":path"] = "/reserved"
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.ConnectAsync(new TunnelClientTransportContext(options, "tunnel-1"), CancellationToken.None).AsTask());

        Assert.Contains("pseudo-headers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcMetadata_IsAvailableOnServerHttpContextRequestHeaders()
    {
        var hostManager = new CapturingTunnelHostManager();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddReverseTunnelGrpcTransport();
        builder.Services.AddSingleton<ITunnelHostManager>(hostManager);

        await using var app = builder.Build();
        app.MapReverseTunnelGrpcTransport();
        await app.StartAsync();

        using var channel = CreateGrpcChannel(app.GetTestServer());
        var client = new TunnelTransport.TunnelTransportClient(channel);
        using var call = client.Connect(new Metadata
        {
            { "authorization", "Bearer jwt" },
            { "x-tunnel-key", "client-key" },
            { "x-route-id", "route-42" }
        });

        await call.RequestStream.WriteAsync(new TunnelMessage
        {
            Connect = new TunnelConnect
            {
                TunnelId = "tunnel-1",
                ConnectionId = "connection-1"
            }
        });

        var context = await hostManager.CapturedContext.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("Bearer jwt", context.Request.Headers["Authorization"].ToString());
        Assert.Equal("client-key", context.Request.Headers["X-Tunnel-Key"].ToString());
        Assert.Equal("route-42", context.Request.Headers["X-Route-Id"].ToString());

        hostManager.Complete();
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task GrpcTunnelClientTransport_UsesExternalCallInvokerAndDoesNotDisposeIt()
    {
        var callInvoker = new RecordingCallInvoker();
        var services = new ServiceCollection()
            .AddSingleton(callInvoker)
            .BuildServiceProvider();
        var transport = new GrpcTunnelClientTransport(
            Options.Create(new ReverseTunnelGrpcTransportOptions
            {
                CallInvokerFactory = provider => provider.GetRequiredService<RecordingCallInvoker>()
            }),
            services);

        var connection = await transport.ConnectAsync(
            new TunnelClientTransportContext(new TunnelClientOptions
            {
                TunnelHost = "https://localhost:7200",
                TunnelId = "shared-channel-tunnel",
                Transport = TunnelTransportKind.Grpc
            }, "shared-channel-tunnel"),
            CancellationToken.None);

        Assert.Equal(1, callInvoker.DuplexCallCount);
        Assert.Equal("shared-channel-tunnel", callInvoker.WrittenConnect?.TunnelId);

        await connection!.DisposeAsync();

        Assert.True(callInvoker.RequestCompleted);
        Assert.True(callInvoker.CallDisposed);
        Assert.False(callInvoker.InvokerDisposed);
    }

    [Fact]
    public void AddReverseTunnelGrpcTransport_ConfiguresKestrelHttp2KeepAliveOptions()
    {
        var services = new ServiceCollection();
        services.AddReverseTunnelGrpcTransport(options =>
        {
            options.ClientKeepAlivePingDelay = TimeSpan.FromSeconds(21);
            options.ClientKeepAlivePingTimeout = TimeSpan.FromSeconds(7);
            options.ServerKeepAlivePingDelay = TimeSpan.FromSeconds(22);
            options.ServerKeepAlivePingTimeout = TimeSpan.FromSeconds(8);
        });

        using var provider = services.BuildServiceProvider();
        var grpcOptions = provider.GetRequiredService<IOptions<ReverseTunnelGrpcTransportOptions>>().Value;
        var kestrelOptions = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(21), grpcOptions.ClientKeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(7), grpcOptions.ClientKeepAlivePingTimeout);
        Assert.Equal(TimeSpan.FromSeconds(22), kestrelOptions.Limits.Http2.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(8), kestrelOptions.Limits.Http2.KeepAlivePingTimeout);
    }

    [Fact]
    public void AddTunnelClient_WithGrpcTransport_ResolvesTunnelClientManager()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITunnelIdProvider>(new StaticTunnelIdProvider("123"));
        services.AddTunnelClient(options => options with
        {
            TunnelHost = "https://localhost:7200",
            TunnelId = "123",
            Transport = TunnelTransportKind.Grpc
        });
        services.AddReverseTunnelGrpcTransport();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var manager = provider.GetRequiredService<ITunnelClientManager>();

        Assert.IsType<TunnelClientManager>(manager);
    }

    [Fact]
    public async Task WebSocketAndGrpcTransports_RunConcurrentlyOnSameHost()
    {
        var collection = new TunnelCollection();
        var registry = new InMemoryTunnelRegistry();
        await using var app = await StartRealTunnelHostAsync(collection, registry);
        var server = app.GetTestServer();

        await using var webSocketTunnel = await ConnectWebSocketTunnelAsync(server, "ws-tunnel");
        await using var grpcTunnel = await ConnectGrpcTunnelAsync(server, "grpc-tunnel");

        await WaitUntilAsync(
            () => collection.TryGetTunnel("ws-tunnel", out _) && collection.TryGetTunnel("grpc-tunnel", out _),
            TimeSpan.FromSeconds(10),
            () => DescribeTunnels(collection));

        var webSocketRegistration = await registry.ResolveAsync("ws-tunnel", CancellationToken.None);
        var grpcRegistration = await registry.ResolveAsync("grpc-tunnel", CancellationToken.None);

        Assert.NotNull(webSocketRegistration);
        Assert.NotNull(grpcRegistration);
        Assert.Equal(TunnelTransportKind.WebSocket, webSocketRegistration.Transport);
        Assert.Equal(TunnelTransportKind.Grpc, grpcRegistration.Transport);
        Assert.Equal("grpc-tunnel-connection", grpcRegistration.ConnectionId);
    }

    [Fact]
    public async Task TunnelHost_KeepsRegistrationAliveBeyondTtlWhileConnected()
    {
        var collection = new TunnelCollection();
        var registry = new InMemoryTunnelRegistry();
        // Short TTL so the renewal loop must fire to keep the registration resolvable.
        var ttl = TimeSpan.FromSeconds(1);
        await using var app = await StartRealTunnelHostAsync(collection, registry, ttl);
        var server = app.GetTestServer();

        await using var webSocketTunnel = await ConnectWebSocketTunnelAsync(server, "long-lived");
        await WaitUntilAsync(
            () => collection.TryGetTunnel("long-lived", out _),
            TimeSpan.FromSeconds(10),
            () => DescribeTunnels(collection));

        // Wait well past the original TTL; without renewal the entry would have expired and
        // been evicted, breaking cross-instance forwarding for long-running tunnels.
        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        var registration = await registry.ResolveAsync("long-lived", CancellationToken.None);
        Assert.NotNull(registration);
        Assert.Equal("test-instance", registration.InstanceId);
        Assert.True(
            registration.ExpiresAt > DateTimeOffset.UtcNow,
            $"Registration expiry {registration.ExpiresAt:o} was not renewed.");
    }

    [Fact]
    public async Task ServerShutdown_CompletesGrpcTunnelPromptlyLikeWebSocket()
    {
        var collection = new TunnelCollection();
        var registry = new InMemoryTunnelRegistry();
        await using var app = await StartRealTunnelHostAsync(collection, registry);
        var server = app.GetTestServer();

        await using var webSocketTunnel = await ConnectWebSocketTunnelAsync(server, "ws-shutdown");
        await using var grpcTunnel = await ConnectGrpcTunnelAsync(server, "grpc-shutdown");
        await WaitUntilAsync(
            () => collection.TryGetTunnel("ws-shutdown", out _) && collection.TryGetTunnel("grpc-shutdown", out _),
            TimeSpan.FromSeconds(10),
            () => DescribeTunnels(collection));

        var stopwatch = Stopwatch.StartNew();
        await app.StopAsync();

#pragma warning disable VSTHRD003 // Completion tasks are produced by MultiplexingStream.
        await AssertCompletesPromptlyAsync(webSocketTunnel.Multiplexing.Completion, "WebSocket");
        await AssertCompletesPromptlyAsync(grpcTunnel.Multiplexing.Completion, "gRPC");
#pragma warning restore VSTHRD003
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Shutdown took {stopwatch.Elapsed}.");
    }

    private static GrpcChannel CreateGrpcChannel(TestServer server) =>
        GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = server.CreateHandler()
        });

    private static async Task<WebApplication> StartRealTunnelHostAsync(
        TunnelCollection collection,
        InMemoryTunnelRegistry registry,
        TimeSpan? registryTtl = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(collection);
        builder.Services.AddSingleton<ITunnelCollectionProvider>(collection);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<ITunnelRegistry>(registry);
        builder.Services.Configure<ReverseTunnelOptions>(options =>
        {
            options.InstanceId = "test-instance";
            options.InternalEndpoint = new Uri("https://test-instance.internal");
            if (registryTtl is { } ttl)
            {
                options.RegistryTtl = ttl;
            }
        });
        builder.Services.AddTunnelForwarder();
        builder.Services.AddReverseTunnelGrpcTransport();

        var app = builder.Build();
        app.MapTunnelHost();
        app.MapReverseTunnelGrpcTransport();
        await app.StartAsync();
        return app;
    }

    private static async Task<ConnectedWebSocketTunnel> ConnectWebSocketTunnelAsync(TestServer server, string tunnelId)
    {
        var socket = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri($"ws://localhost/tunnel/{tunnelId}"), CancellationToken.None);
        var multiplexing = await MultiplexingStream.CreateAsync(
                socket.AsStream(),
                new MultiplexingStream.Options { ProtocolMajorVersion = 3 })
            .WaitAsync(TimeSpan.FromSeconds(3));

        return new ConnectedWebSocketTunnel(socket, multiplexing);
    }

    private static async Task<ConnectedGrpcTunnel> ConnectGrpcTunnelAsync(TestServer server, string tunnelId)
    {
        var channel = CreateGrpcChannel(server);
        var client = new TunnelTransport.TunnelTransportClient(channel);
        var call = client.Connect();
        var transportStream = new GrpcTunnelTransportStream(
            call.ResponseStream,
            call.RequestStream,
            tunnelId,
            $"{tunnelId}-connection",
            async () =>
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                call.Dispose();
                channel.Dispose();
            });
        await transportStream.WriteConnectAsync();
        var multiplexing = await MultiplexingStream.CreateAsync(
                transportStream,
                new MultiplexingStream.Options { ProtocolMajorVersion = 3 })
            .WaitAsync(TimeSpan.FromSeconds(3));

        return new ConnectedGrpcTunnel(channel, call, transportStream, multiplexing);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string>? describe = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Condition was not met within {timeout}. {describe?.Invoke()}");
        }
    }

    private static string DescribeTunnels(TunnelCollection collection) =>
        "Active tunnels: " + string.Join(", ", collection.Select(item => item.TunnelId));

    private static async Task AssertCompletesPromptlyAsync(Task completion, string transport)
    {
#pragma warning disable VSTHRD003 // The completion task is intentionally supplied by the transport under test.
        var completed = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(3)));
#pragma warning restore VSTHRD003
        Assert.True(ReferenceEquals(completion, completed), $"{transport} tunnel did not complete promptly.");
        if (completion.IsFaulted)
        {
            _ = completion.Exception;
        }
    }

    private sealed class RecordingCallInvoker : CallInvoker, IDisposable
    {
        public int DuplexCallCount { get; private set; }
        public TunnelConnect? WrittenConnect { get; private set; }
        public bool RequestCompleted { get; private set; }
        public bool CallDisposed { get; private set; }
        public bool InvokerDisposed { get; private set; }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options)
        {
            DuplexCallCount++;
            var requestStream = new RecordingClientStreamWriter<TRequest>(message =>
            {
                if (message is TunnelMessage { KindCase: TunnelMessage.KindOneofCase.Connect } tunnelMessage)
                {
                    WrittenConnect = tunnelMessage.Connect;
                }
            }, () => RequestCompleted = true);

            return new AsyncDuplexStreamingCall<TRequest, TResponse>(
                requestStream,
                new EmptyAsyncStreamReader<TResponse>(),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => new Metadata(),
                () => CallDisposed = true);
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) =>
            throw new NotSupportedException();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) =>
            throw new NotSupportedException();

        public void Dispose() => InvokerDisposed = true;
    }

    private sealed class RecordingClientStreamWriter<T>(Action<T> onWrite, Action onComplete) : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            onWrite(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            onComplete();
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        public T Current => default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class StaticTunnelIdProvider(string tunnelId) : ITunnelIdProvider
    {
        public ValueTask<string?> GetTunnelIdAsync() => ValueTask.FromResult<string?>(tunnelId);
    }

    private sealed class CapturingTunnelHostManager : ITunnelHostManager
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpContext> CapturedContext { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ReverseTunnel.Yarp.Tunnel.Tunnel?> GetOrCreateTunnelAsync(
            HttpContext context,
            string tunnelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReverseTunnel.Yarp.Tunnel.Tunnel?>(null);

        public Task StartTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default)
        {
            CapturedContext.TrySetResult(context);
            return completion.Task.WaitAsync(cancellationToken);
        }

        public Task StartTunnelAsync(
            TunnelTransportConnection connection,
            string tunnelId,
            HttpContext? context,
            TunnelTransportKind transportKind,
            string? connectionId = null,
            CancellationToken cancellationToken = default)
        {
            CapturedContext.TrySetResult(context ?? new DefaultHttpContext());
            return completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => completion.TrySetResult();
    }

    private sealed class ConnectedWebSocketTunnel(WebSocket socket, MultiplexingStream multiplexing) : IAsyncDisposable
    {
        public MultiplexingStream Multiplexing { get; } = multiplexing;

        public async ValueTask DisposeAsync()
        {
            await Multiplexing.DisposeAsync().ConfigureAwait(false);
            socket.Dispose();
        }
    }

    private sealed class ConnectedGrpcTunnel(
        GrpcChannel channel,
        AsyncDuplexStreamingCall<TunnelMessage, TunnelMessage> call,
        GrpcTunnelTransportStream transportStream,
        MultiplexingStream multiplexing) : IAsyncDisposable
    {
        public MultiplexingStream Multiplexing { get; } = multiplexing;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Multiplexing.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                transportStream.Dispose();
                call.Dispose();
                channel.Dispose();
            }
        }
    }
}

