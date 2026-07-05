using System.Net.WebSockets;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class WebSocketTunnelClientTransport(ITunnelClientFactory clientFactory) : ITunnelClientTransport
{
    public TunnelTransportKind Kind => TunnelTransportKind.WebSocket;

    public async ValueTask<TunnelTransportConnection?> ConnectAsync(
        TunnelClientTransportContext context,
        CancellationToken cancellationToken)
    {
        var webSocket = await clientFactory.CreateAsync().ConfigureAwait(false);
        if (webSocket is null)
        {
            return null;
        }

        var uri = await clientFactory.GetUriAsync().ConfigureAwait(false);
        try
        {
            await webSocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            webSocket.Dispose();
            throw new TunnelTransportException("Connection timed out", uri, 0);
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpRequestException httpRequestException)
        {
            var responseBody = await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri).ConfigureAwait(false);
            webSocket.Dispose();
            throw new TunnelTransportException(
                httpRequestException.Message,
                uri,
                (int?)webSocket.HttpStatusCode,
                responseBody,
                ex);
        }
        catch (WebSocketException ex)
        {
            var responseBody = await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri).ConfigureAwait(false);
            webSocket.Dispose();
            throw new TunnelTransportException(
                ex.Message,
                uri,
                (int?)webSocket.HttpStatusCode,
                responseBody,
                ex);
        }

        return new TunnelTransportConnection(
            webSocket.AsStream(),
            uri,
            async () =>
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                webSocket.Dispose();
            },
            webSocket.Dispose);
    }

    private async Task<string> TryFetchErrorResponseBodyAsync(string wsUrl)
    {
        try
        {
            var httpUrl = wsUrl.Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase);
            using var httpClient = clientFactory.CreateHttpClient();
            using var response = await httpClient.GetAsync(httpUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }

        return string.Empty;
    }
}
