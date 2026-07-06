using Grpc.Core;
using Sample.Chat.Shared;

namespace Sample.Chat.Server.Services;

public sealed class ChatRoomServiceImpl(ChatState state, ILogger<ChatRoomServiceImpl> logger)
    : ChatRoomService.ChatRoomServiceBase
{
    public override Task<CreateRoomResponse> CreateRoom(CreateRoomRequest request, ServerCallContext context)
    {
        var room = state.CreateRoom(request.DisplayName);
        logger.LogInformation("CreateRoom returned {RoomId}", room.RoomId);
        return Task.FromResult(new CreateRoomResponse
        {
            RoomId = room.RoomId,
            DisplayName = room.DisplayName
        });
    }

    public override Task<ListRoomsResponse> ListRooms(ListRoomsRequest request, ServerCallContext context)
    {
        var response = new ListRoomsResponse();
        response.Rooms.AddRange(state.ListRooms());
        return Task.FromResult(response);
    }

    public override Task<JoinRoomResponse> JoinRoom(JoinRoomRequest request, ServerCallContext context)
    {
        var accepted = state.TryGetRoom(request.RoomId, out _);
        logger.LogInformation("JoinRoom {RoomId} by {UserName}: {Accepted}", request.RoomId, request.UserName, accepted);
        return Task.FromResult(new JoinRoomResponse
        {
            RoomId = request.RoomId,
            UserName = request.UserName,
            Accepted = accepted
        });
    }
}