using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel;

namespace ReverseTunnel.Yarp.Tests;

public class TunnelClientOptionsStoreTests
{
    [Fact]
    public void Constructor_StoresInitialOptions()
    {
        var options = new TunnelClientOptions { TunnelId = "test-id", TunnelHost = "wss://localhost:7200" };
        var store = new TunnelClientOptionsStore(options);

        Assert.Equal("test-id", store.Current.TunnelId);
        Assert.Equal("wss://localhost:7200", store.Current.TunnelHost);
    }

    [Fact]
    public void Update_ChangesCurrentOptions()
    {
        var initialOptions = new TunnelClientOptions { TunnelId = "initial" };
        var store = new TunnelClientOptionsStore(initialOptions);

        store.Update(current => current with { TunnelId = "updated" });

        Assert.Equal("updated", store.Current.TunnelId);
    }

    [Fact]
    public void Update_FiresOptionsChangedEvent()
    {
        var initialOptions = new TunnelClientOptions { TunnelId = "old" };
        var store = new TunnelClientOptionsStore(initialOptions);

        (TunnelClientOptions OldOptions, TunnelClientOptions NewOptions)? eventArgs = null;
        store.OptionsChanged += (_, args) => eventArgs = args;

        store.Update(current => current with { TunnelId = "new" });

        Assert.NotNull(eventArgs);
        Assert.Equal("old", eventArgs.Value.OldOptions.TunnelId);
        Assert.Equal("new", eventArgs.Value.NewOptions.TunnelId);
    }

    [Fact]
    public void Update_WithNullReturn_ThrowsArgumentNullException()
    {
        var store = new TunnelClientOptionsStore(new TunnelClientOptions());

        Assert.Throws<ArgumentNullException>(() => store.Update(_ => null!));
    }

    [Fact]
    public void Update_NoChange_StillFiresEvent()
    {
        var options = new TunnelClientOptions { TunnelId = "same" };
        var store = new TunnelClientOptionsStore(options);

        int eventCount = 0;
        store.OptionsChanged += (_, _) => eventCount++;

        store.Update(current => current); // same instance

        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void Current_IsThreadSafe_UnderConcurrentUpdates()
    {
        var store = new TunnelClientOptionsStore(new TunnelClientOptions { TunnelId = "0" });
        int updates = 100;

        Parallel.For(0, updates, i =>
        {
            store.Update(current => current with { TunnelId = i.ToString() });
        });

        // Just verify no exceptions and options is a valid state
        Assert.NotNull(store.Current);
        Assert.NotNull(store.Current.TunnelId);
    }
}
