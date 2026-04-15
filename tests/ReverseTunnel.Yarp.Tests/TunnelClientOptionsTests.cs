using ReverseTunnel.Yarp.Tunnel;

namespace ReverseTunnel.Yarp.Tests;

public class TunnelClientOptionsTests
{
    [Fact]
    public void DefaultOptions_HaveExpectedValues()
    {
        var options = new TunnelClientOptions();

        Assert.Null(options.TunnelId);
        Assert.Null(options.TunnelHost);
        Assert.Equal("/tunnel/{0}", options.TunnelPathTemplate);
        Assert.True(options.IsEnabled);
        Assert.NotNull(options.RequestHeaders);
        Assert.Empty(options.RequestHeaders);
        Assert.Null(options.WebSocketOptions);
    }

    [Fact]
    public void WithExpression_CopiesAndUpdatesProperties()
    {
        var original = new TunnelClientOptions
        {
            TunnelId = "id1",
            TunnelHost = "wss://example.com"
        };

        var updated = original with { TunnelId = "id2" };

        Assert.Equal("id2", updated.TunnelId);
        Assert.Equal("wss://example.com", updated.TunnelHost);
    }

    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        var options = new TunnelClientOptions();
        Assert.True(options.IsEnabled);
    }

    [Fact]
    public void IsEnabled_CanBeSetToFalse()
    {
        var options = new TunnelClientOptions { IsEnabled = false };
        Assert.False(options.IsEnabled);
    }

    [Fact]
    public void RequestHeaders_CanBePopulated()
    {
        var options = new TunnelClientOptions
        {
            RequestHeaders = new Dictionary<string, string>
            {
                { "Authorization", "Bearer token123" },
                { "X-Custom-Header", "value" }
            }
        };

        Assert.Equal(2, options.RequestHeaders.Count);
        Assert.Equal("Bearer token123", options.RequestHeaders["Authorization"]);
    }
}
