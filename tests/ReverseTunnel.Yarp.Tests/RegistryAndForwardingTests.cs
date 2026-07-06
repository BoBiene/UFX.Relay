using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;
using ReverseTunnel.Yarp.Tunnel.Registry;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tests;

public class RegistryAndForwardingTests
{
    [Fact]
    public async Task InMemoryRegistry_ResolvesTunnelOwner()
    {
        var registry = new InMemoryTunnelRegistry();
        var now = DateTimeOffset.UtcNow;
        var registration = new TunnelRegistration(
            "tunnel-1",
            "instance-a",
            new Uri("https://instance-a:8080"),
            TunnelTransportKind.WebSocket,
            now,
            now.AddMinutes(1),
            "connection-1");

        await registry.RegisterAsync(registration, CancellationToken.None);

        var resolved = await registry.ResolveAsync("tunnel-1", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("instance-a", resolved.InstanceId);
        Assert.Equal(new Uri("https://instance-a:8080"), resolved.InternalEndpoint);
    }

    [Fact]
    public async Task InMemoryRegistry_DoesNotResolveExpiredRegistration()
    {
        var registry = new InMemoryTunnelRegistry();
        var now = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(new TunnelRegistration(
            "stale",
            "instance-a",
            new Uri("https://instance-a:8080"),
            TunnelTransportKind.WebSocket,
            now.AddMinutes(-2),
            now.AddMinutes(-1)), CancellationToken.None);

        var resolved = await registry.ResolveAsync("stale", CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task InMemoryRegistry_RenewSlidesExpiryForwardPreservingTtl()
    {
        var registry = new InMemoryTunnelRegistry();
        var now = DateTimeOffset.UtcNow;
        // A one-minute TTL window with only five seconds left before it would expire.
        await registry.RegisterAsync(new TunnelRegistration(
            "tunnel-1",
            "instance-a",
            new Uri("https://instance-a:8080"),
            TunnelTransportKind.WebSocket,
            now.AddSeconds(-55),
            now.AddSeconds(5)), CancellationToken.None);

        await registry.RenewAsync("tunnel-1", "instance-a", CancellationToken.None);

        var resolved = await registry.ResolveAsync("tunnel-1", CancellationToken.None);
        Assert.NotNull(resolved);
        // Renewal must push the expiry roughly a full TTL window (one minute) into the future.
        Assert.True(
            resolved.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(50),
            $"Renew did not extend expiry; ExpiresAt was {resolved.ExpiresAt:o}.");
    }

    [Fact]
    public async Task InMemoryRegistry_RenewIgnoresDifferentInstance()
    {
        var registry = new InMemoryTunnelRegistry();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(5);
        await registry.RegisterAsync(new TunnelRegistration(
            "tunnel-1",
            "instance-a",
            new Uri("https://instance-a:8080"),
            TunnelTransportKind.WebSocket,
            now.AddSeconds(-55),
            expiresAt), CancellationToken.None);

        await registry.RenewAsync("tunnel-1", "instance-b", CancellationToken.None);

        var resolved = await registry.ResolveAsync("tunnel-1", CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(expiresAt, resolved.ExpiresAt);
    }

    [Fact]
    public async Task InMemoryRegistry_RenewMissingEntryIsNoop()
    {
        var registry = new InMemoryTunnelRegistry();

        await registry.RenewAsync("does-not-exist", "instance-a", CancellationToken.None);

        Assert.Null(await registry.ResolveAsync("does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task InternalForwarder_ForwardsRequestToOwnerInstance()
    {
        var registry = new InMemoryTunnelRegistry();
        var now = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(new TunnelRegistration(
            "tunnel-1",
            "instance-a",
            new Uri("https://instance-a:8080"),
            TunnelTransportKind.WebSocket,
            now,
            now.AddMinutes(1)), CancellationToken.None);

        HttpRequestMessage? forwardedRequest = null;
        var handler = new DelegateHandler(request =>
        {
            forwardedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("forwarded", Encoding.UTF8, "text/plain")
            };
        });
        var forwarder = CreateForwarder(registry, handler);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("public.example");
        context.Request.Path = "/arty/tunnel-1/client";
        context.Request.QueryString = new QueryString("?x=1");
        context.Response.Body = new MemoryStream();

        var handled = await forwarder.TryForwardAsync(context, "tunnel-1", CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal(new Uri("https://instance-a:8080/arty/tunnel-1/client?x=1"), forwardedRequest?.RequestUri);
        Assert.True(forwardedRequest?.Headers.Contains("X-ReverseTunnel-Forwarded-By"));
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Equal("forwarded", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task InternalForwarder_RejectsForwardingLoop()
    {
        var registry = new InMemoryTunnelRegistry();
        var now = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(new TunnelRegistration(
            "tunnel-1",
            "instance-a",
            new Uri("https://instance-a:8080"),
            TunnelTransportKind.WebSocket,
            now,
            now.AddMinutes(1)), CancellationToken.None);

        var handler = new DelegateHandler(_ => throw new InvalidOperationException("Should not forward loops."));
        var forwarder = CreateForwarder(registry, handler);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-ReverseTunnel-Forwarded-By"] = "instance-b";

        var handled = await forwarder.TryForwardAsync(context, "tunnel-1", CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status508LoopDetected, context.Response.StatusCode);
    }

    [Fact]
    public async Task InternalForwarder_PropagatesCancellation()
    {
        var registry = new InMemoryTunnelRegistry();
        var now = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(new TunnelRegistration(
            "tunnel-1",
            "instance-a",
            new Uri("https://instance-a:8080"),
            TunnelTransportKind.WebSocket,
            now,
            now.AddMinutes(1)), CancellationToken.None);

        var handler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var forwarder = CreateForwarder(registry, handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            forwarder.TryForwardAsync(context, "tunnel-1", cts.Token));
    }

    private static InternalTunnelRequestForwarder CreateForwarder(ITunnelRegistry registry, HttpMessageHandler handler)
    {
        var options = Options.Create(new ReverseTunnelOptions
        {
            InstanceId = "instance-b",
            InternalEndpoint = new Uri("https://instance-b:8080")
        });
        return new InternalTunnelRequestForwarder(
            registry,
            new ReverseTunnelInstanceInfo(options),
            options,
            handler);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}