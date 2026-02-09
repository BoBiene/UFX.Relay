using Yarp.ReverseProxy.Forwarder;

namespace Sample.Blazor.Gateway
{
    internal sealed class PathOverrideTransformer : HttpTransformer
    {
        private readonly string _path;
        public PathOverrideTransformer(string path) => _path = path;

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            // Override path+query explicitly (this is the critical part)
            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
                destinationPrefix,
                _path,
                httpContext.Request.QueryString);

            // Optional: don't forward original Host header
            proxyRequest.Headers.Host = null;
        }
    }
}
