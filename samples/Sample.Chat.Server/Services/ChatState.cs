using System.Collections.Concurrent;
using Grpc.Core;
using Sample.Chat.Shared;

namespace Sample.Chat.Server.Services;

public sealed class ChatState(ILogger<ChatState> logger)
{
    private readonly ConcurrentDictionary<string, ChatRoom> rooms = new();
    private readonly ConcurrentDictionary<string, List<ChatMessage>> messagesByRoom = new();
    private readonly ConcurrentDictionary<string, ChatConnection> connections = new();

    public ChatRoom CreateRoom(string? displayName)
    {
        var room = new ChatRoom
        {
            RoomId = $"room-{Guid.NewGuid():N}"[..13],
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "General" : displayName.Trim()
        };

        rooms[room.RoomId] = room;
        messagesByRoom.TryAdd(room.RoomId, []);
        logger.LogInformation("Room created: {RoomId} ({DisplayName})", room.RoomId, room.DisplayName);
        _ = BroadcastRoomCreatedAsync(room, CancellationToken.None);
        return room;
    }

    public IReadOnlyCollection<ChatRoom> ListRooms()
    {
        EnsureDefaultRoom();
        return rooms.Values.OrderBy(room => room.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool TryGetRoom(string roomId, out ChatRoom room)
    {
        EnsureDefaultRoom();
        return rooms.TryGetValue(roomId, out room!);
    }

    public ChatMessage AddMessage(string roomId, string userName, string text)
    {
        var message = new ChatMessage
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            RoomId = roomId,
            UserName = string.IsNullOrWhiteSpace(userName) ? "Guest" : userName.Trim(),
            Text = text.Trim(),
            UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var messages = messagesByRoom.GetOrAdd(roomId, _ => []);
        lock (messages)
        {
            messages.Add(message);
            if (messages.Count > 50)
            {
                messages.RemoveRange(0, messages.Count - 50);
            }
        }

        logger.LogInformation("Message sent in {RoomId} by {UserName}", roomId, message.UserName);
        _ = BroadcastMessageAsync(message, CancellationToken.None);
        return message;
    }

    public IReadOnlyCollection<ChatMessage> GetRecentMessages(string roomId, int count)
    {
        var take = count <= 0 ? 20 : Math.Min(count, 50);
        if (!messagesByRoom.TryGetValue(roomId, out var messages))
        {
            return [];
        }

        lock (messages)
        {
            return messages.TakeLast(take).ToArray();
        }
    }

    public async Task<ChatConnection> AddConnectionAsync(
        string roomId,
        string userName,
        IServerStreamWriter<ChatServerEvent> writer,
        CancellationToken cancellationToken)
    {
        EnsureDefaultRoom();
        if (!rooms.ContainsKey(roomId))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Room '{roomId}' was not found."));
        }

        var connection = new ChatConnection(
            $"chat-{Guid.NewGuid():N}",
            roomId,
            string.IsNullOrWhiteSpace(userName) ? "Guest" : userName.Trim(),
            writer);
        connections[connection.ConnectionId] = connection;

        await connection.WriteAsync(new ChatServerEvent
        {
            Welcome = new ChatServerWelcome
            {
                ConnectionId = connection.ConnectionId,
                RoomId = connection.RoomId,
                UserName = connection.UserName
            }
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Chat stream connected: {ConnectionId} ({UserName})", connection.ConnectionId, connection.UserName);
        await BroadcastAsync(connection.RoomId, new ChatServerEvent
        {
            UserJoined = new ChatUserJoined
            {
                RoomId = connection.RoomId,
                UserName = connection.UserName
            }
        }, cancellationToken).ConfigureAwait(false);

        return connection;
    }

    public async Task RemoveConnectionAsync(ChatConnection connection, CancellationToken cancellationToken)
    {
        if (!connections.TryRemove(connection.ConnectionId, out _))
        {
            return;
        }

        logger.LogInformation("Chat stream disconnected: {ConnectionId} ({UserName})", connection.ConnectionId, connection.UserName);
        await BroadcastAsync(connection.RoomId, new ChatServerEvent
        {
            UserLeft = new ChatUserLeft
            {
                RoomId = connection.RoomId,
                UserName = connection.UserName
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task BroadcastTypingAsync(string roomId, string userName, bool isTyping, CancellationToken cancellationToken) =>
        BroadcastAsync(roomId, new ChatServerEvent
        {
            TypingReceived = new ChatTypingReceived
            {
                RoomId = roomId,
                UserName = userName,
                IsTyping = isTyping
            }
        }, cancellationToken);

    private Task BroadcastMessageAsync(ChatMessage message, CancellationToken cancellationToken) =>
        BroadcastAsync(message.RoomId, new ChatServerEvent
        {
            MessageReceived = new ChatMessageReceived { Message = message }
        }, cancellationToken);

    private Task BroadcastRoomCreatedAsync(ChatRoom room, CancellationToken cancellationToken) =>
        BroadcastAsync(null, new ChatServerEvent
        {
            RoomCreated = new ChatRoomCreated { Room = room }
        }, cancellationToken);

    private async Task BroadcastAsync(string? roomId, ChatServerEvent serverEvent, CancellationToken cancellationToken)
    {
        var targets = connections.Values
            .Where(connection => roomId is null || connection.RoomId == roomId)
            .ToArray();

        foreach (var target in targets)
        {
            try
            {
                await target.WriteAsync(serverEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Dropping chat event for {ConnectionId}", target.ConnectionId);
            }
        }
    }

    private void EnsureDefaultRoom()
    {
        if (rooms.IsEmpty)
        {
            var room = new ChatRoom
            {
                RoomId = "general",
                DisplayName = "General"
            };
            rooms.TryAdd(room.RoomId, room);
            messagesByRoom.TryAdd(room.RoomId, []);
        }
    }
}

public sealed class ChatConnection(
    string connectionId,
    string roomId,
    string userName,
    IServerStreamWriter<ChatServerEvent> writer)
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public string ConnectionId { get; } = connectionId;
    public string RoomId { get; } = roomId;
    public string UserName { get; } = userName;

    public async Task WriteAsync(ChatServerEvent serverEvent, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteAsync(serverEvent).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }
}