using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Tunnel.Registry;

namespace ReverseTunnel.Yarp.Tunnel.Forwarder;

public sealed class InternalTunnelRequestForwarder(
    ITunnelRegistry tunnelRegistry,
    ReverseTunnelInstanceInfo instanceInfo,
    IOptions<ReverseTunnelOptions> options,
    HttpMessageHandler? httpMessageHandler = null)
{
    private readonly HttpClient httpClient = httpMessageHandler is null
        ? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        }, true)
        : new HttpClient(httpMessageHandler, false);

    public async Task<bool> TryForwardAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken)
    {
        var registration = await tunnelRegistry.ResolveAsync(tunnelId, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return false;
        }

        if (registration.InstanceId == instanceInfo.InstanceId)
        {
            return false;
        }

        var forwardedByHeader = options.Value.ForwardedByHeader;
        if (context.Request.Headers.ContainsKey(forwardedByHeader))
        {
            context.Response.StatusCode = StatusCodes.Status508LoopDetected;
            return true;
        }

        if (registration.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await tunnelRegistry.UnregisterAsync(tunnelId, registration.InstanceId, cancellationToken).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }

        using var request = CreateForwardRequest(context, registration.InternalEndpoint, forwardedByHeader);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        context.Response.StatusCode = (int)response.StatusCode;
        CopyHeaders(response.Headers, context.Response.Headers);
        if (response.Content is not null)
        {
            CopyHeaders(response.Content.Headers, context.Response.Headers);
            await response.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        }

        context.Response.Headers.Remove("transfer-encoding");
        return true;
    }

    private HttpRequestMessage CreateForwardRequest(HttpContext context, Uri internalEndpoint, string forwardedByHeader)
    {
        var target = new UriBuilder(internalEndpoint)
        {
            Path = context.Request.PathBase.Add(context.Request.Path).ToString(),
            Query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] : string.Empty
        }.Uri;

        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (HttpMethods.IsPost(context.Request.Method) ||
            HttpMethods.IsPut(context.Request.Method) ||
            HttpMethods.IsPatch(context.Request.Method))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, forwardedByHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        request.Headers.TryAddWithoutValidation(forwardedByHeader, instanceInfo.InstanceId);
        return request;
    }

    private static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            destination[header.Key] = header.Value.ToArray();
        }
    }
}
