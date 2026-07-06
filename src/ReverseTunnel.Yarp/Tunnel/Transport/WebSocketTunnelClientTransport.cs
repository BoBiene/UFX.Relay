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
            var responseBody = await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
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
            var responseBody = await TryFetchErrorResponseBodyAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
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

    private async Task<string> TryFetchErrorResponseBodyAsync(string wsUrl, CancellationToken cancellationToken)
    {
        try
        {
            var httpUrl = wsUrl.Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase);
            using var httpClient = clientFactory.CreateHttpClient();
            // Bound this diagnostic fetch: it must never keep a superseded reconnect attempt
            // alive. The cancellation token lets a Reset() abort it promptly, and the timeout
            // caps it well below the default 100s when the endpoint is unreachable/black-holed.
            if (httpClient.Timeout == Timeout.InfiniteTimeSpan || httpClient.Timeout > TimeSpan.FromSeconds(10))
            {
                httpClient.Timeout = TimeSpan.FromSeconds(10);
            }
            using var response = await httpClient.GetAsync(httpUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }

        return string.Empty;
    }
}
