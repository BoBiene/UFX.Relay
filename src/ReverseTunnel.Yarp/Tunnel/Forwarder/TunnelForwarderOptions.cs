using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ReverseTunnel.Yarp.Tunnel.Forwarder;

public class TunnelForwarderOptions
{
    public delegate string? GetTunnelIdFromHttpContextDelegate(TunnelForwarderOptions options, HttpContext context);

    /// <summary>
    /// Invoked when a request could not be forwarded and the response has not started, so the host
    /// can produce its own result - a redirect to a "not connected" page, or a gateway status.
    /// </summary>
    public delegate Task ForwarderErrorHandler(HttpContext context, string tunnelId, ForwarderError error, Exception? exception);

    public string? DefaultTunnelId { get; set; }
    public string TunnelIdHeader { get; set; } = "TunnelId";
    public Action<TransformBuilderContext>? Transformer { get; set; }

    public GetTunnelIdFromHttpContextDelegate? TunnelIdFromContext { get; set; }
        = (options, context) => options.DefaultTunnelId ?? context.GetTunnelIdFromHost();

    /// <summary>
    /// How long a forwarded request waits for the peer to accept its channel. Kept below
    /// <c>SocketsHttpHandler.ConnectTimeout</c> so the offer fails with a
    /// <see cref="TunnelChannelOfferTimeoutException"/> rather than an anonymous cancellation.
    /// </summary>
    public TimeSpan ChannelOfferTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether a channel offer that times out removes the tunnel from its collection, so the next
    /// request takes the not-connected path.
    /// </summary>
    public bool InvalidateTunnelOnOfferTimeout { get; set; } = true;

    /// <summary>Produces a defined response when forwarding fails.</summary>
    public ForwarderErrorHandler? OnForwarderError { get; set; }
}
