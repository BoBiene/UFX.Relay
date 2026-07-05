using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Grpc.Protocol;
using ReverseTunnel.Yarp.Grpc.Transport;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Grpc;

public sealed class GrpcTunnelClientTransport(IOptions<ReverseTunnelGrpcTransportOptions> options) : ITunnelClientTransport
{
    public TunnelTransportKind Kind => TunnelTransportKind.Grpc;

    public async ValueTask<TunnelTransportConnection?> ConnectAsync(
        TunnelClientTransportContext context,
        CancellationToken cancellationToken)
    {
        if (context.Options.TunnelHost is null)
        {
            throw new ArgumentNullException(nameof(context.Options.TunnelHost));
        }

        var metadata = GrpcMetadataValidator.CreateRequestMetadata(context.Options.RequestHeaders);
        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = options.Value.ClientKeepAlivePingDelay,
                KeepAlivePingTimeout = options.Value.ClientKeepAlivePingTimeout,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
            }
        };
        options.Value.ConfigureChannel?.Invoke(channelOptions);
        var baseAddress = ToHttpUri(context.Options.TunnelHost);
        var channel = GrpcChannel.ForAddress(baseAddress, channelOptions);
        var client = new TunnelTransport.TunnelTransportClient(channel);
        var call = client.Connect(metadata, cancellationToken: cancellationToken);
        var connectionId = $"{options.Value.ConnectionIdPrefix}-{Guid.NewGuid():N}";
        var stream = new GrpcTunnelTransportStream(
            call.ResponseStream,
            call.RequestStream,
            context.TunnelId,
            connectionId,
            async () =>
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                call.Dispose();
                channel.Dispose();
            });

        try
        {
            await stream.WriteConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new TunnelTransportConnection(stream, baseAddress);
    }

    private static Uri ToHttpUri(string host)
    {
        var builder = new UriBuilder(host);
        if (builder.Scheme == "ws") builder.Scheme = Uri.UriSchemeHttp;
        if (builder.Scheme == "wss") builder.Scheme = Uri.UriSchemeHttps;
        return builder.Uri;
    }
}
