using Grpc.Core;
using Sample.Chat.Shared;

namespace Sample.Chat.Server.Services;

public sealed class ChatStreamServiceImpl(ChatState state, ILogger<ChatStreamServiceImpl> logger)
    : ChatStreamService.ChatStreamServiceBase
{
    public override async Task Connect(
        IAsyncStreamReader<ChatClientEvent> requestStream,
        IServerStreamWriter<ChatServerEvent> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The chat stream must start with a hello event."));
        }

        var firstEvent = requestStream.Current;
        if (firstEvent.KindCase != ChatClientEvent.KindOneofCase.Hello)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first chat stream event must be hello."));
        }

        var hello = firstEvent.Hello;
        var connection = await state.AddConnectionAsync(
            hello.RoomId,
            hello.UserName,
            responseStream,
            context.CancellationToken).ConfigureAwait(false);

        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var clientEvent = requestStream.Current;
                switch (clientEvent.KindCase)
                {
                    case ChatClientEvent.KindOneofCase.Message:
                        var message = clientEvent.Message;
                        state.AddMessage(message.RoomId, message.UserName, message.Text);
                        break;
                    case ChatClientEvent.KindOneofCase.Typing:
                        var typing = clientEvent.Typing;
                        await state.BroadcastTypingAsync(
                            typing.RoomId,
                            typing.UserName,
                            typing.IsTyping,
                            context.CancellationToken).ConfigureAwait(false);
                        break;
                    case ChatClientEvent.KindOneofCase.Leave:
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Chat stream cancelled: {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            await state.RemoveConnectionAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
    }
}