using Microsoft.Extensions.Options;
using UFX.Relay.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel.Forwarder;

public class ForwarderTunnelIdProvider(IHttpContextAccessor accessor, IOptions<TunnelForwarderOptions> options)
    : ITunnelIdProvider
{
    public ValueTask<string?> GetTunnelIdAsync()
    {
        return new(accessor.HttpContext == null ? null : GetFromQuery() ?? GetFromHeader() ?? GetFromOptions());
        string? GetFromQuery() => accessor.HttpContext.Request.Query[options.Value.TunnelIdHeader].FirstOrDefault();
        string? GetFromHeader() => accessor.HttpContext.Request.Headers[options.Value.TunnelIdHeader].FirstOrDefault();
        string? GetFromOptions() => options.Value.TunnelIdFromContext?.Invoke(options.Value, accessor.HttpContext);
    }
}