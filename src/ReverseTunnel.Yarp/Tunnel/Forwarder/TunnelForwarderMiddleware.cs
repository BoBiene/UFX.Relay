using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Abstractions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ReverseTunnel.Yarp.Tunnel.Forwarder;

public class TunnelForwarderMiddleware(
    IHttpForwarder forwarder,
    TunnelForwarderHttpClientFactory clientFactory,
    ITransformBuilder builder,
    IOptions<TunnelForwarderOptions> options,
    ITunnelIdProvider tunnelIdProvider,
    ITunnelHostManager tunnelManager,
    InternalTunnelRequestForwarder internalForwarder) : IMiddleware
{
    private HttpTransformer? transformer;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var tunnelId = await tunnelIdProvider.GetTunnelIdAsync().ConfigureAwait(false);
        if (tunnelId == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var tunnel = await tunnelManager.GetOrCreateTunnelAsync(context, tunnelId, context.RequestAborted).ConfigureAwait(false);
        if (tunnel == null)
        {
            if (await internalForwarder.TryForwardAsync(context, tunnelId, context.RequestAborted).ConfigureAwait(false))
            {
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var client = clientFactory.CreateClient(new ForwarderHttpClientContext { NewConfig = HttpClientConfig.Empty });
        var destinationPrefix = $"http://{context.Request.Host}";
        if (options.Value.Transformer != null) transformer ??= builder.Create(options.Value.Transformer);
        _ = await forwarder.SendAsync(context, destinationPrefix, client, ForwarderRequestConfig.Empty, transformer ?? HttpTransformer.Default).ConfigureAwait(false);
    }
}