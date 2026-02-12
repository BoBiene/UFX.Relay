using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel;

public static class HttpContextExtensions
{
    private static readonly HashSet<string> excludedHosts = new() { "localhost", "api", "auth", "id", "user", "login", "www", "test", "dev", "staging", "prod", "production", "relay", "tunnel"};
    
    public static string? GetTunnelIdFromHost(this HttpContext context) => GetTunnelIdFromHost(context.Request.Host.Host);
    public static string? GetTunnelIdFromHost(this Uri uri)=> GetTunnelIdFromHost(uri.Host);
    private static string? GetTunnelIdFromHost(this string host)
    {
        var hostnameType = Uri.CheckHostName(host.ToLowerInvariant());
        if (hostnameType != UriHostNameType.Dns) return null;
        var parts = host.Split('.');
        return parts.Length <= 1 || excludedHosts.Contains(parts[0]) ? null : parts[0];
    }

    /// <summary>
    /// Checks if the current request was received through a UFX.Relay tunnel connection.
    /// </summary>
    /// <param name="context">The HttpContext to check</param>
    /// <returns>True if the request came through the tunnel, false if it came through a normal HTTP endpoint</returns>
    public static bool IsFromTunnel(this HttpContext context)
    {
        return context.Features.Get<ITunnelRequestFeature>() != null;
    }

    /// <summary>
    /// Gets the tunnel request feature if the request came through a tunnel.
    /// </summary>
    /// <param name="context">The HttpContext to check</param>
    /// <returns>The ITunnelRequestFeature if the request came through the tunnel, null otherwise</returns>
    public static ITunnelRequestFeature? GetTunnelRequestFeature(this HttpContext context)
    {
        return context.Features.Get<ITunnelRequestFeature>();
    }
}