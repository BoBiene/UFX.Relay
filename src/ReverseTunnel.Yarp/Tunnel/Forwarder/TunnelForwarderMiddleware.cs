using Microsoft.Extensions.Logging;
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
    InternalTunnelRequestForwarder internalForwarder,
    ILogger<TunnelForwarderMiddleware> logger) : IMiddleware
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

        // Disposed once the response is complete; SendAsync does not return until then.
        using var client = clientFactory.CreateClient(new ForwarderHttpClientContext { NewConfig = HttpClientConfig.Empty });
        var destinationPrefix = $"http://{context.Request.Host}";
        if (options.Value.Transformer != null) transformer ??= builder.Create(options.Value.Transformer);

        var error = await forwarder
            .SendAsync(context, destinationPrefix, client, ForwarderRequestConfig.Empty, transformer ?? HttpTransformer.Default)
            .ConfigureAwait(false);

        if (error == ForwarderError.None) return;

        var exception = context.GetForwarderErrorFeature()?.Exception;
        logger.LogInformation(
            "Forwarding over tunnel {TunnelId} failed with {ForwarderError} for {Path}.",
            tunnelId,
            error,
            context.Request.Path.Value ?? string.Empty);

        // Nothing can be said to the caller once the response has started.
        if (context.Response.HasStarted) return;

        if (options.Value.OnForwarderError is { } handler)
        {
            await handler(context, tunnelId, error, exception).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
}
