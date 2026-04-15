using Microsoft.AspNetCore.Http;
using ReverseTunnel.Yarp.Tunnel;

namespace ReverseTunnel.Yarp.Tests;

public class HttpContextExtensionsTests
{
    [Theory]
    [InlineData("tenant1.example.com", "tenant1")]
    [InlineData("customer.myapp.io", "customer")]
    [InlineData("abc.subdomain.example.com", "abc")]
    public void GetTunnelIdFromHost_ReturnsFirstSubdomain_ForValidDnsHosts(string host, string expectedTunnelId)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);

        var tunnelId = context.GetTunnelIdFromHost();

        Assert.Equal(expectedTunnelId, tunnelId);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("api.example.com")]
    [InlineData("auth.example.com")]
    [InlineData("www.example.com")]
    [InlineData("test.example.com")]
    [InlineData("dev.example.com")]
    [InlineData("staging.example.com")]
    [InlineData("prod.example.com")]
    [InlineData("production.example.com")]
    [InlineData("relay.example.com")]
    [InlineData("tunnel.example.com")]
    [InlineData("login.example.com")]
    [InlineData("user.example.com")]
    [InlineData("id.example.com")]
    public void GetTunnelIdFromHost_ReturnsNull_ForExcludedHosts(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);

        var tunnelId = context.GetTunnelIdFromHost();

        Assert.Null(tunnelId);
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void GetTunnelIdFromHost_ReturnsNull_ForIpAddresses(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);

        var tunnelId = context.GetTunnelIdFromHost();

        Assert.Null(tunnelId);
    }

    [Fact]
    public void GetTunnelIdFromHost_ReturnsNull_ForSingleSegmentHostname()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("myhost");

        var tunnelId = context.GetTunnelIdFromHost();

        Assert.Null(tunnelId);
    }

    [Fact]
    public void GetTunnelIdFromHost_Uri_ReturnsFirstSubdomain()
    {
        var uri = new Uri("https://tenant1.example.com/path");

        var tunnelId = uri.GetTunnelIdFromHost();

        Assert.Equal("tenant1", tunnelId);
    }

    [Fact]
    public void GetTunnelIdFromHost_Uri_ReturnsNull_ForExcludedHost()
    {
        var uri = new Uri("https://localhost/path");

        var tunnelId = uri.GetTunnelIdFromHost();

        Assert.Null(tunnelId);
    }

    [Fact]
    public void IsFromTunnel_ReturnsFalse_ForNormalRequest()
    {
        var context = new DefaultHttpContext();

        var result = context.IsFromTunnel();

        Assert.False(result);
    }

    [Fact]
    public void IsFromTunnel_ReturnsTrue_WhenTunnelFeatureSet()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<ReverseTunnel.Yarp.Abstractions.ITunnelRequestFeature>(
            new FakeTunnelRequestFeature());

        var result = context.IsFromTunnel();

        Assert.True(result);
    }

    [Fact]
    public void GetTunnelRequestFeature_ReturnsNull_WhenNotSet()
    {
        var context = new DefaultHttpContext();

        var feature = context.GetTunnelRequestFeature();

        Assert.Null(feature);
    }

    [Fact]
    public void GetTunnelRequestFeature_ReturnsFeature_WhenSet()
    {
        var context = new DefaultHttpContext();
        var expected = new FakeTunnelRequestFeature();
        context.Features.Set<ReverseTunnel.Yarp.Abstractions.ITunnelRequestFeature>(expected);

        var feature = context.GetTunnelRequestFeature();

        Assert.Same(expected, feature);
    }

    private sealed class FakeTunnelRequestFeature : ReverseTunnel.Yarp.Abstractions.ITunnelRequestFeature
    {
        public string TunnelId => "fake-tunnel";
    }
}
