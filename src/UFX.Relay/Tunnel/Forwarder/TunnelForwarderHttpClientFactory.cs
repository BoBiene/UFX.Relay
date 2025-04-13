using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Nerdbank.Streams;
using UFX.Relay.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace UFX.Relay.Tunnel.Forwarder;


public class TunnelForwarderHttpClientFactory(ITunnelHostManager tunnelManager, IHttpContextAccessor accessor, ITunnelIdProvider tunnelIdProvider) : IForwarderHttpClientFactory
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
                var channel = await tunnel.GetChannelAsync(tunnel is TunnelHost ? httpContext.Connection.Id : null, token);
                return channel.AsStream();
            },
        };
        return new HttpMessageInvoker(handler, true);
    }
}
