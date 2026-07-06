using Grpc.Core;
using Sample.Chat.Shared;

namespace Sample.Chat.Server.Services;

public sealed class ChatMessageServiceImpl(ChatState state, ILogger<ChatMessageServiceImpl> logger)
    : ChatMessageService.ChatMessageServiceBase
{
    public override Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
    {
        if (!state.TryGetRoom(request.RoomId, out _))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Room '{request.RoomId}' was not found."));
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Message text is required."));
        }

        var message = state.AddMessage(request.RoomId, request.UserName, request.Text);
        logger.LogInformation("SendMessage returned {MessageId}", message.MessageId);
        return Task.FromResult(new SendMessageResponse { Message = message });
    }

    public override Task<GetRecentMessagesResponse> GetRecentMessages(GetRecentMessagesRequest request, ServerCallContext context)
    {
        var response = new GetRecentMessagesResponse();
        response.Messages.AddRange(state.GetRecentMessages(request.RoomId, request.Count));
        return Task.FromResult(response);
    }
}