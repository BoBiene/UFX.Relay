using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace ReverseTunnel.Yarp.Tunnel.Forwarder;


public class TunnelForwarderHttpClientFactory(
    ITunnelHostManager tunnelManager,
    IHttpContextAccessor accessor,
    ITunnelIdProvider tunnelIdProvider,
    ITunnelCollectionProvider tunnelCollectionProvider,
    IOptions<TunnelForwarderOptions> options,
    ILogger<TunnelForwarderHttpClientFactory> logger) : IForwarderHttpClientFactory
{

    //TODO: Consider creating a pool of HttpMessageInvoker instances to reuse up to the limit of a MultiplexingStream channel limit
    // effectively there should be a 1-2-1 relationship between the HttpMessageInvoker and the MultiplexingStream channel
    // If/when a HttpMessageInvoker is disposed replace with a new instance from the same channel?
    // The pool will need to be cleared when the MultiplexingStream/relay websocket connection is closed
    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context)
    {
        var httpContext = accessor.HttpContext;
        if (httpContext == null)
            throw new BadHttpRequestException("The HttpContext must not be null.");
        SocketsHttpHandler handler = new SocketsHttpHandler()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ActivityHeadersPropagator = (DistributedContextPropagator)new ReverseProxyPropagator(DistributedContextPropagator.Current),
            //NOTE: This is the timeout for the initial connection to the relay, it could be a multiple of the websocket retry delay in TunnelManager for X number of attempts
            ConnectTimeout = TimeSpan.FromSeconds(15.0),
            //Note: may maintain a pool of channelId's here and pass the channelid to GetStreamAsync => RelayConnection.GetChannel
            ConnectCallback = async (ctx, token) =>
            {
                var relayId = await tunnelIdProvider.GetTunnelIdAsync() ?? throw new KeyNotFoundException();
                var tunnel = await tunnelManager.GetOrCreateTunnelAsync(httpContext, relayId, token);
                if (tunnel == null) throw new ConnectionAbortedException($"Tunnel {relayId} not found");
                try
                {
                    var channel = await tunnel.GetChannelAsync(
                        tunnel is TunnelHost ? httpContext.Connection.Id : null,
                        options.Value.ChannelOfferTimeout,
                        token);
                    return channel.AsStream();
                }
                catch (TunnelChannelOfferTimeoutException ex)
                {
                    // The tunnel looks connected but does not serve channels, so it is taken out of
                    // service instead of failing every following request the same way.
                    await TakeOutOfServiceAsync(httpContext, relayId, tunnel, ex);
                    throw new ConnectionAbortedException(ex.Message, ex);
                }
            },
        };
        return new HttpMessageInvoker(handler, true);
    }

    private async Task TakeOutOfServiceAsync(HttpContext httpContext, string tunnelId, Tunnel tunnel, TunnelChannelOfferTimeoutException ex)
    {
        logger.LogInformation(
            "Tunnel {TunnelId} for host {Host} did not accept a channel within {TimeoutSeconds}s; taking it out of service. {Diagnostics}",
            tunnelId,
            httpContext.Request.Host.Value,
            ex.Timeout.TotalSeconds,
            tunnel.GetDiagnostics());

        tunnel.Invalidate("channel offer timed out");

        if (!options.Value.InvalidateTunnelOnOfferTimeout) return;

        try
        {
            var tunnels = await tunnelCollectionProvider.GetTunnelCollectionAsync(httpContext, CancellationToken.None).ConfigureAwait(false);
            if (!tunnels.TryRemoveTunnel((tunnelId, tunnel))) return;

            // Detached: disposal closes the transport, which the request must not wait for.
            _ = Task.Run(async () =>
            {
                try { await tunnel.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) { logger.LogDebug(disposeEx, "Disposing unusable tunnel {TunnelId} failed.", tunnelId); }
            });
        }
        catch (Exception removeEx)
        {
            logger.LogDebug(removeEx, "Removing unusable tunnel {TunnelId} failed.", tunnelId);
        }
    }
}
