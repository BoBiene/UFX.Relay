using Microsoft.AspNetCore.HttpOverrides;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Transforms;

namespace UFX.Relay.Tunnel.Forwarder
{
    public partial class TunnelPathPrefixTransformer(string prefix) : RequestTransform
    {
        private const string REGEX_ID = "id";
        private const string REGEX_PATH = "path";
        public string Prefix { get; } = prefix;
        private readonly Regex _pathRegex = new($@"^/{prefix.TrimStart('/').TrimEnd('/')}/(?<{REGEX_ID}>[^/]+)(?:(?<{REGEX_PATH}>/.*))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string? GetTunnelIdFromContext(TunnelForwarderOptions options, HttpContext context)
        {
            var match = _pathRegex.Match(context.Request.Path);
            if (match.Success)
                return match.Groups[REGEX_ID].Value;
            return options.DefaultTunnelId;
        }

        public override ValueTask ApplyAsync(RequestTransformContext context)
        {
            var path = context.HttpContext.Request.Path.Value ?? string.Empty;
            var match = _pathRegex.Match(path);
            if (match.Success)
            {
                var localPath = match.Groups[REGEX_PATH].Value;
                context.Path = localPath;
                context.HttpContext.Request.PathBase = path[..^localPath.Length];
                AddHeader(context, ForwardedHeadersDefaults.XForwardedPrefixHeaderName, path[..^localPath.Length]);
            }
            return ValueTask.CompletedTask;
        }
    }
}
