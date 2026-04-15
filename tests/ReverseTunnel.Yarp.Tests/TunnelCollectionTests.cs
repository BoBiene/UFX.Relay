using ReverseTunnel.Yarp.Tunnel;
using TunnelClass = ReverseTunnel.Yarp.Tunnel.Tunnel;

namespace ReverseTunnel.Yarp.Tests;

public class TunnelCollectionTests
{
    [Fact]
    public void GetOrAdd_AddsTunnel_WhenNotPresent()
    {
        var collection = new TunnelCollection();
        var tunnel = CreateFakeTunnel();

        var result = collection.GetOrAdd("tunnel1", tunnel);

        Assert.True(collection.TryGetTunnel("tunnel1", out var stored));
        Assert.Same(tunnel, stored);
        Assert.Same(tunnel, result);
    }

    [Fact]
    public void GetOrAdd_ReturnsExisting_WhenAlreadyPresent()
    {
        var collection = new TunnelCollection();
        var first = CreateFakeTunnel();
        var second = CreateFakeTunnel();

        collection.GetOrAdd("tunnel1", first);
        var result = collection.GetOrAdd("tunnel1", second);

        Assert.Same(first, result);
        second.Dispose();
    }

    [Fact]
    public void GetOrAdd_WithFactory_AddsTunnel_WhenNotPresent()
    {
        var collection = new TunnelCollection();
        var tunnel = CreateFakeTunnel();

        var result = collection.GetOrAdd("tunnel1", _ => tunnel);

        Assert.Same(tunnel, result);
        Assert.True(collection.TryGetTunnel("tunnel1", out _));
    }

    [Fact]
    public void AddOrUpdate_AddsTunnel_WhenNotPresent()
    {
        var collection = new TunnelCollection();
        var tunnel = CreateFakeTunnel();

        var result = collection.AddOrUpdate("tunnel1", _ => tunnel, (_, old) => old);

        Assert.Same(tunnel, result);
        Assert.True(collection.TryGetTunnel("tunnel1", out var stored));
        Assert.Same(tunnel, stored);
    }

    [Fact]
    public void AddOrUpdate_UpdatesTunnel_WhenAlreadyPresent()
    {
        var collection = new TunnelCollection();
        var first = CreateFakeTunnel();
        var second = CreateFakeTunnel();

        collection.GetOrAdd("tunnel1", first);
        var result = collection.AddOrUpdate("tunnel1", _ => first, (_, _) => second);

        Assert.Same(second, result);
        Assert.True(collection.TryGetTunnel("tunnel1", out var stored));
        Assert.Same(second, stored);

        first.Dispose();
    }

    [Fact]
    public void TryGetTunnel_ReturnsFalse_WhenNotPresent()
    {
        var collection = new TunnelCollection();

        var found = collection.TryGetTunnel("nonexistent", out var tunnel);

        Assert.False(found);
        Assert.Null(tunnel);
    }

    [Fact]
    public void TryRemoveTunnel_RemovesTunnel_WhenPresent()
    {
        var collection = new TunnelCollection();
        var tunnel = CreateFakeTunnel();
        collection.GetOrAdd("tunnel1", tunnel);

        var removed = collection.TryRemoveTunnel(("tunnel1", tunnel));

        Assert.True(removed);
        Assert.False(collection.TryGetTunnel("tunnel1", out _));
    }

    [Fact]
    public void TryRemoveTunnel_ReturnsFalse_WhenTunnelMismatch()
    {
        var collection = new TunnelCollection();
        var first = CreateFakeTunnel();
        var other = CreateFakeTunnel();
        collection.GetOrAdd("tunnel1", first);

        // Trying to remove a different tunnel instance for the same key should fail
        var removed = collection.TryRemoveTunnel(("tunnel1", other));

        Assert.False(removed);
        Assert.True(collection.TryGetTunnel("tunnel1", out _));

        other.Dispose();
    }

    [Fact]
    public void TryRemoveTunnelById_RemovesTunnel_WhenPresent()
    {
        var collection = new TunnelCollection();
        var tunnel = CreateFakeTunnel();
        collection.GetOrAdd("tunnel1", tunnel);

        var removed = collection.TryRemoveTunnelById("tunnel1", out var removedTunnel);

        Assert.True(removed);
        Assert.Same(tunnel, removedTunnel);
        Assert.False(collection.TryGetTunnel("tunnel1", out _));
    }

    [Fact]
    public void TryRemoveTunnelById_ReturnsFalse_WhenNotPresent()
    {
        var collection = new TunnelCollection();

        var removed = collection.TryRemoveTunnelById("nonexistent", out var removedTunnel);

        Assert.False(removed);
        Assert.Null(removedTunnel);
    }

    [Fact]
    public void GetEnumerator_ReturnsAllTunnels()
    {
        var collection = new TunnelCollection();
        var tunnel1 = CreateFakeTunnel();
        var tunnel2 = CreateFakeTunnel();

        collection.GetOrAdd("id1", tunnel1);
        collection.GetOrAdd("id2", tunnel2);

        var items = collection.ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.TunnelId == "id1" && ReferenceEquals(i.Tunnel, tunnel1));
        Assert.Contains(items, i => i.TunnelId == "id2" && ReferenceEquals(i.Tunnel, tunnel2));
    }

    [Fact]
    public async Task GetTunnelCollectionAsync_ReturnsSelf()
    {
        var collection = new TunnelCollection();
        var httpContext = new DefaultHttpContext();

        var result = await collection.GetTunnelCollectionAsync(httpContext);

        Assert.Same(collection, result);
    }

    private static TunnelClass CreateFakeTunnel()
    {
        return new FakeTunnel();
    }

    /// <summary>
    /// A minimal Tunnel subclass backed by a real (but disconnected from any real peer) MultiplexingStream.
    /// Both sides of a FullDuplexStream pair are created so the MXS handshake can complete.
    /// </summary>
    private sealed class FakeTunnel : TunnelClass
    {
        public FakeTunnel() : base(CreateStream()) { }

        private static Nerdbank.Streams.MultiplexingStream CreateStream()
        {
            var (a, b) = Nerdbank.Streams.FullDuplexStream.CreatePair();
            var options = new Nerdbank.Streams.MultiplexingStream.Options { ProtocolMajorVersion = 3 };
            var t1 = Nerdbank.Streams.MultiplexingStream.CreateAsync(a, options);
            var t2 = Nerdbank.Streams.MultiplexingStream.CreateAsync(b, options);
            Task.WhenAll(t1, t2).GetAwaiter().GetResult();
            return t1.Result;
        }
    }
}
