using System.IO.Pipes;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ReverseTunnel.Yarp.Tunnel.Forwarder;

public class TunnelForwarderOptions
{
    public delegate string? GetTunnelIdFromHttpContextDelegate(TunnelForwarderOptions options, HttpContext context);
    public string? DefaultTunnelId { get; set; }
    public string TunnelIdHeader { get; set; } = "TunnelId";
    public Action<TransformBuilderContext>? Transformer { get; set; }

    public GetTunnelIdFromHttpContextDelegate? TunnelIdFromContext { get; set; }
        = (options, context) => options.DefaultTunnelId ?? context.GetTunnelIdFromHost();
}