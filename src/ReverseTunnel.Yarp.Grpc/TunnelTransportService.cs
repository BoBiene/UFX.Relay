using Grpc.Core;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Grpc.Protocol;
using ReverseTunnel.Yarp.Grpc.Transport;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Grpc;

public sealed class TunnelTransportService(ITunnelHostManager tunnelHostManager, ILogger<TunnelTransportService> logger)
    : TunnelTransport.TunnelTransportBase
{
    public override async Task Connect(
        IAsyncStreamReader<TunnelMessage> requestStream,
        IServerStreamWriter<TunnelMessage> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The tunnel stream ended before the connect message."));
        }

        var firstMessage = requestStream.Current;
        if (firstMessage.KindCase != TunnelMessage.KindOneofCase.Connect)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The tunnel stream must start with a connect message."));
        }

        var connect = firstMessage.Connect;
        if (string.IsNullOrWhiteSpace(connect.TunnelId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The connect message must include a tunnel id."));
        }

        var stream = new GrpcTunnelTransportStream(requestStream, responseStream);
        var connection = new TunnelTransportConnection(stream, null);

        try
        {
            await tunnelHostManager.StartTunnelAsync(
                connection,
                connect.TunnelId,
                context.GetHttpContext(),
                TunnelTransportKind.Grpc,
                connect.ConnectionId,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("gRPC tunnel {TunnelId} was cancelled.", connect.TunnelId);
        }
    }
}