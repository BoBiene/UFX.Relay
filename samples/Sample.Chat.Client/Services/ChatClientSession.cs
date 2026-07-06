using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Sample.Chat.Shared;

namespace Sample.Chat.Client.Services;

public sealed class ChatClientSession(
    ChatRoomService.ChatRoomServiceClient roomClient,
    ChatMessageService.ChatMessageServiceClient messageClient,
    ChatStreamService.ChatStreamServiceClient streamClient,
    GrpcChannel channel,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IOptions<ChatClientResilienceOptions> resilienceOptions,
    ILogger<ChatClientSession> logger) : IAsyncDisposable
{
    private readonly ChatClientResilienceOptions resilience = resilienceOptions.Value;
    private readonly List<ChatRoom> rooms = [];
    private readonly List<ChatMessage> messages = [];
    private readonly List<string> eventLog = [];
    private readonly List<string> typingUsers = [];
    private readonly object sync = new();
    private readonly object streamStateSync = new();
    private readonly SemaphoreSlim streamWriteLock = new(1, 1);
    private AsyncDuplexStreamingCall<ChatClientEvent, ChatServerEvent>? streamCall;
    private CancellationTokenSource? activeStreamCts;
    private CancellationTokenSource? streamLoopCts;
    private Task? streamLoopTask;
    private string chatStreamStatus = "Disconnected";
    private int reconnectAttempt;
    private DateTimeOffset? nextReconnectAt;
    private bool disposed;

    public event Action? Changed;

    public string ServerUrl => configuration["ChatServer:Url"] ?? "https://localhost:7300";
    public int SharedChannelHashCode => channel.GetHashCode();
    public string UserName { get; set; } = Environment.UserName is { Length: > 0 } name ? name : "Guest";
    public string? CurrentRoomId { get; private set; }
    public bool IsChatStreamConnected { get; private set; }
    public string? TunnelTestResponse { get; private set; }

    public string ChatStreamStatus
    {
        get { lock (sync) return chatStreamStatus; }
    }

    public int ReconnectAttempt
    {
        get { lock (sync) return reconnectAttempt; }
    }

    public DateTimeOffset? NextReconnectAt
    {
        get { lock (sync) return nextReconnectAt; }
    }

    public IReadOnlyList<ChatRoom> Rooms
    {
        get { lock (sync) return rooms.ToArray(); }
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (sync) return messages.ToArray(); }
    }

    public IReadOnlyList<string> EventLog
    {
        get { lock (sync) return eventLog.ToArray(); }
    }

    public IReadOnlyList<string> TypingUsers
    {
        get { lock (sync) return typingUsers.ToArray(); }
    }

    public IReadOnlyList<string> SharedChannelUsage { get; } =
    [
        "TunnelTransport.Connect",
        "ChatRoomService.CreateRoom",
        "ChatRoomService.ListRooms",
        "ChatMessageService.SendMessage",
        "ChatStreamService.Connect"
    ];

    public async Task RefreshRoomsAsync()
    {
        try
        {
            var response = await roomClient.ListRoomsAsync(
                new ListRoomsRequest(),
                deadline: CreateDeadline()).ResponseAsync.ConfigureAwait(false);

            lock (sync)
            {
                rooms.Clear();
                rooms.AddRange(response.Rooms);
            }
            AddEvent($"Listed {response.Rooms.Count} room(s) through ChatRoomService.ListRooms.");
        }
        catch (Exception ex) when (IsExpectedCallFailure(ex))
        {
            AddEvent($"List rooms failed: {FormatCallFailure(ex)}");
        }
        finally
        {
            NotifyChanged();
        }
    }

    public async Task<ChatRoom?> CreateRoomAsync(string displayName)
    {
        try
        {
            var response = await roomClient.CreateRoomAsync(
                new CreateRoomRequest
                {
                    DisplayName = displayName
                },
                deadline: CreateDeadline()).ResponseAsync.ConfigureAwait(false);

            var room = new ChatRoom
            {
                RoomId = response.RoomId,
                DisplayName = response.DisplayName
            };

            lock (sync)
            {
                UpsertRoom(room);
            }
            AddEvent($"Room created through ChatRoomService.CreateRoom: {room.DisplayName}.");
            NotifyChanged();
            return room;
        }
        catch (Exception ex) when (IsExpectedCallFailure(ex))
        {
            AddEvent($"Create room failed: {FormatCallFailure(ex)}");
            NotifyChanged();
            return null;
        }
    }

    public async Task JoinRoomAsync(string roomId, string userName)
    {
        UserName = string.IsNullOrWhiteSpace(userName) ? "Guest" : userName.Trim();

        try
        {
            var join = await roomClient.JoinRoomAsync(
                new JoinRoomRequest
                {
                    RoomId = roomId,
                    UserName = UserName
                },
                deadline: CreateDeadline()).ResponseAsync.ConfigureAwait(false);

            if (!join.Accepted)
            {
                AddEvent($"Join rejected for room {roomId}.");
                NotifyChanged();
                return;
            }
        }
        catch (Exception ex) when (IsExpectedCallFailure(ex))
        {
            AddEvent($"Join failed: {FormatCallFailure(ex)}");
            NotifyChanged();
            return;
        }

        await StopStreamLoopAsync(sendLeave: true).ConfigureAwait(false);
        CurrentRoomId = roomId;
        await LoadRecentMessagesAsync(roomId, addEvent: false).ConfigureAwait(false);
        StartStreamLoop(roomId, UserName);
        AddEvent("Chat stream supervisor started for ChatStreamService.Connect.");
        NotifyChanged();
    }

    public async Task SendUnaryMessageAsync(string text)
    {
        if (CurrentRoomId is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await messageClient.SendMessageAsync(
                new SendMessageRequest
                {
                    RoomId = CurrentRoomId,
                    UserName = UserName,
                    Text = text
                },
                deadline: CreateDeadline()).ResponseAsync.ConfigureAwait(false);
            AddEvent("Message sent through ChatMessageService.SendMessage.");
        }
        catch (Exception ex) when (IsExpectedCallFailure(ex))
        {
            AddEvent($"Send message failed: {FormatCallFailure(ex)}");
        }
        finally
        {
            NotifyChanged();
        }
    }

    public async Task SendStreamMessageAsync(string text)
    {
        if (CurrentRoomId is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var writeTimeout = new CancellationTokenSource(GetPositiveTimeout(resilience.StreamWriteTimeout, TimeSpan.FromSeconds(5)));
        if (await TryWriteStreamEventAsync(new ChatClientEvent
        {
            Message = new ChatClientMessage
            {
                RoomId = CurrentRoomId,
                UserName = UserName,
                Text = text
            }
        }, logFailures: true, writeTimeout.Token).ConfigureAwait(false))
        {
            AddEvent("Message sent through ChatStreamService.Connect.");
            NotifyChanged();
        }
    }

    public async Task SendTypingAsync(bool isTyping)
    {
        if (CurrentRoomId is null)
        {
            return;
        }

        using var writeTimeout = new CancellationTokenSource(GetPositiveTimeout(resilience.StreamWriteTimeout, TimeSpan.FromSeconds(5)));
        await TryWriteStreamEventAsync(new ChatClientEvent
        {
            Typing = new ChatClientTyping
            {
                RoomId = CurrentRoomId,
                UserName = UserName,
                IsTyping = isTyping
            }
        }, logFailures: false, writeTimeout.Token).ConfigureAwait(false);
    }

    public async Task TestTunnelAsync()
    {
        try
        {
            var client = httpClientFactory.CreateClient("TunnelTest");
            TunnelTestResponse = await client.GetStringAsync("/client-info").ConfigureAwait(false);
            AddEvent("Tunnel test request succeeded through the server forwarder.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            TunnelTestResponse = null;
            AddEvent($"Tunnel test failed: {ex.Message}");
        }
        finally
        {
            NotifyChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await StopStreamLoopAsync(sendLeave: true).ConfigureAwait(false);
        streamWriteLock.Dispose();
    }

    private void StartStreamLoop(string roomId, string userName)
    {
        var loopCts = new CancellationTokenSource();
        streamLoopCts = loopCts;
        streamLoopTask = Task.Run(() => MaintainStreamAsync(roomId, userName, loopCts.Token));
    }

    private async Task MaintainStreamAsync(string roomId, string userName, CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            SetStreamStatus(attempt == 1 ? "Connecting" : $"Reconnecting ({attempt})", connected: false, attempt, nextReconnect: null);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            AsyncDuplexStreamingCall<ChatClientEvent, ChatServerEvent>? call = null;

            try
            {
                call = streamClient.Connect(cancellationToken: attemptCts.Token);
                SetActiveStream(call, attemptCts);

                var helloWritten = await TryWriteStreamEventAsync(new ChatClientEvent
                {
                    Hello = new ChatClientHello
                    {
                        RoomId = roomId,
                        UserName = userName
                    }
                }, logFailures: true, attemptCts.Token).ConfigureAwait(false);

                if (!helloWritten)
                {
                    throw new RpcException(new Status(StatusCode.Unavailable, "Unable to write chat stream hello."));
                }

                AddEvent("Chat stream opened through ChatStreamService.Connect.");
                attempt = 0;
                await LoadRecentMessagesAsync(roomId, addEvent: false).ConfigureAwait(false);

                while (await call.ResponseStream.MoveNext(attemptCts.Token).ConfigureAwait(false))
                {
                    ApplyServerEvent(call.ResponseStream.Current);
                    NotifyChanged();
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    AddEvent("Chat stream completed by server.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (cancellationToken.IsCancellationRequested || ex.StatusCode == StatusCode.Cancelled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                AddEvent($"Chat stream canceled: {FormatCallFailure(ex)}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat stream ended with an error.");
                AddEvent($"Chat stream error: {FormatCallFailure(ex)}");
            }
            finally
            {
                await ClearActiveStreamAsync(call).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var delay = ComputeReconnectDelay(attempt);
            var nextReconnect = DateTimeOffset.Now.Add(delay);
            SetStreamStatus($"Reconnecting in {FormatDelay(delay)}", connected: false, attempt, nextReconnect);
            AddEvent($"Chat stream reconnect scheduled in {FormatDelay(delay)}.");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        SetStreamStatus("Disconnected", connected: false, attempt: 0, nextReconnect: null);
    }

    private async Task<bool> LoadRecentMessagesAsync(string roomId, bool addEvent)
    {
        try
        {
            var recent = await messageClient.GetRecentMessagesAsync(
                new GetRecentMessagesRequest
                {
                    RoomId = roomId,
                    Count = 20
                },
                deadline: CreateDeadline()).ResponseAsync.ConfigureAwait(false);

            lock (sync)
            {
                messages.Clear();
                messages.AddRange(recent.Messages);
            }

            if (addEvent)
            {
                AddEvent($"Loaded {recent.Messages.Count} recent message(s).");
            }

            NotifyChanged();
            return true;
        }
        catch (Exception ex) when (IsExpectedCallFailure(ex))
        {
            AddEvent($"Load recent messages failed: {FormatCallFailure(ex)}");
            NotifyChanged();
            return false;
        }
    }

    private async Task<bool> TryWriteStreamEventAsync(ChatClientEvent clientEvent, bool logFailures, CancellationToken cancellationToken)
    {
        try
        {
            await streamWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (logFailures)
            {
                AddEvent("Chat stream write timed out before it could start.");
                NotifyChanged();
            }
            return false;
        }

        try
        {
            AsyncDuplexStreamingCall<ChatClientEvent, ChatServerEvent>? call;
            CancellationTokenSource? callCts;
            lock (streamStateSync)
            {
                call = streamCall;
                callCts = activeStreamCts;
            }

            if (call is null || callCts is null || callCts.IsCancellationRequested)
            {
                if (logFailures)
                {
                    AddEvent("Chat stream is not connected; event was not sent.");
                    NotifyChanged();
                }
                return false;
            }

            await call.RequestStream.WriteAsync(clientEvent).WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (logFailures)
            {
                AddEvent("Chat stream write timed out or was canceled.");
                NotifyChanged();
            }
            CancelActiveStream();
            return false;
        }
        catch (Exception ex) when (IsExpectedCallFailure(ex) || ex is InvalidOperationException)
        {
            if (logFailures)
            {
                AddEvent($"Chat stream write failed: {FormatCallFailure(ex)}");
                NotifyChanged();
            }
            CancelActiveStream();
            return false;
        }
        finally
        {
            streamWriteLock.Release();
        }
    }

    private async Task StopStreamLoopAsync(bool sendLeave)
    {
        var loopCts = streamLoopCts;
        var loopTask = streamLoopTask;
        streamLoopCts = null;
        streamLoopTask = null;

        if (sendLeave && CurrentRoomId is not null)
        {
            using var writeTimeout = new CancellationTokenSource(GetPositiveTimeout(resilience.StreamWriteTimeout, TimeSpan.FromSeconds(5)));
            await TryWriteStreamEventAsync(new ChatClientEvent
            {
                Leave = new ChatClientLeave
                {
                    RoomId = CurrentRoomId,
                    UserName = UserName
                }
            }, logFailures: false, writeTimeout.Token).ConfigureAwait(false);
            await TryCompleteActiveRequestStreamAsync(writeTimeout.Token).ConfigureAwait(false);
        }

        if (loopCts is not null)
        {
            await loopCts.CancelAsync().ConfigureAwait(false);
        }
        CancelActiveStream();

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        loopCts?.Dispose();
        SetStreamStatus("Disconnected", connected: false, attempt: 0, nextReconnect: null);
    }

    private async Task TryCompleteActiveRequestStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            await streamWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            AsyncDuplexStreamingCall<ChatClientEvent, ChatServerEvent>? call;
            lock (streamStateSync)
            {
                call = streamCall;
            }

            if (call is not null)
            {
                await call.RequestStream.CompleteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            streamWriteLock.Release();
        }
    }

    private void ApplyServerEvent(ChatServerEvent serverEvent)
    {
        lock (sync)
        {
            switch (serverEvent.KindCase)
            {
                case ChatServerEvent.KindOneofCase.Welcome:
                    IsChatStreamConnected = true;
                    chatStreamStatus = "Connected";
                    reconnectAttempt = 0;
                    nextReconnectAt = null;
                    AddEventLocked($"Welcome received for {serverEvent.Welcome.UserName}.");
                    break;
                case ChatServerEvent.KindOneofCase.RoomCreated:
                    UpsertRoom(serverEvent.RoomCreated.Room);
                    AddEventLocked($"Room announced: {serverEvent.RoomCreated.Room.DisplayName}.");
                    break;
                case ChatServerEvent.KindOneofCase.UserJoined:
                    AddEventLocked($"{serverEvent.UserJoined.UserName} joined {serverEvent.UserJoined.RoomId}.");
                    break;
                case ChatServerEvent.KindOneofCase.UserLeft:
                    AddEventLocked($"{serverEvent.UserLeft.UserName} left {serverEvent.UserLeft.RoomId}.");
                    break;
                case ChatServerEvent.KindOneofCase.MessageReceived:
                    messages.Add(serverEvent.MessageReceived.Message);
                    AddEventLocked($"Message received from {serverEvent.MessageReceived.Message.UserName}.");
                    break;
                case ChatServerEvent.KindOneofCase.TypingReceived:
                    UpdateTyping(serverEvent.TypingReceived.UserName, serverEvent.TypingReceived.IsTyping);
                    break;
                case ChatServerEvent.KindOneofCase.Error:
                    AddEventLocked($"Server error: {serverEvent.Error.Message}");
                    break;
            }
        }
    }

    private void SetActiveStream(
        AsyncDuplexStreamingCall<ChatClientEvent, ChatServerEvent> call,
        CancellationTokenSource cancellationTokenSource)
    {
        lock (streamStateSync)
        {
            streamCall = call;
            activeStreamCts = cancellationTokenSource;
        }
    }

    private async Task ClearActiveStreamAsync(AsyncDuplexStreamingCall<ChatClientEvent, ChatServerEvent>? call)
    {
        if (call is null)
        {
            return;
        }

        await streamWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (streamStateSync)
            {
                if (ReferenceEquals(streamCall, call))
                {
                    streamCall = null;
                    activeStreamCts = null;
                }
            }
            call.Dispose();
        }
        finally
        {
            streamWriteLock.Release();
        }
    }

    private void CancelActiveStream()
    {
        CancellationTokenSource? callCts;
        lock (streamStateSync)
        {
            callCts = activeStreamCts;
        }

        try
        {
            if (callCts is { IsCancellationRequested: false })
            {
                callCts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetStreamStatus(string status, bool connected, int attempt, DateTimeOffset? nextReconnect)
    {
        lock (sync)
        {
            chatStreamStatus = status;
            IsChatStreamConnected = connected;
            reconnectAttempt = attempt;
            nextReconnectAt = nextReconnect;

            if (!connected)
            {
                typingUsers.Clear();
            }
        }
        NotifyChanged();
    }

    private DateTime CreateDeadline() => DateTime.UtcNow.Add(GetPositiveTimeout(resilience.UnaryCallTimeout, TimeSpan.FromSeconds(10)));

    private TimeSpan ComputeReconnectDelay(int attempt)
    {
        var initial = GetPositiveTimeout(resilience.StreamInitialReconnectDelay, TimeSpan.FromSeconds(1));
        var max = GetPositiveTimeout(resilience.StreamMaxReconnectDelay, TimeSpan.FromSeconds(30));
        var multiplier = Math.Max(1, resilience.StreamBackoffMultiplier);
        var exponent = Math.Max(0, attempt - 1);
        var delayMs = initial.TotalMilliseconds * Math.Pow(multiplier, exponent);
        delayMs = Math.Min(delayMs, max.TotalMilliseconds);

        var jitter = resilience.StreamReconnectJitter <= TimeSpan.Zero
            ? TimeSpan.Zero
            : resilience.StreamReconnectJitter;
        if (jitter > TimeSpan.Zero)
        {
            var jitterMs = Math.Min(jitter.TotalMilliseconds, delayMs / 2);
            delayMs += Random.Shared.NextDouble() * jitterMs;
        }

        return TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
    }

    private static TimeSpan GetPositiveTimeout(TimeSpan configured, TimeSpan fallback) =>
        configured > TimeSpan.Zero ? configured : fallback;

    private static string FormatDelay(TimeSpan delay) =>
        delay < TimeSpan.FromSeconds(1)
            ? $"{delay.TotalMilliseconds:0} ms"
            : $"{delay.TotalSeconds:0.0} s";

    private static bool IsExpectedCallFailure(Exception ex) =>
        ex is RpcException or HttpRequestException or TaskCanceledException or TimeoutException;

    private static string FormatCallFailure(Exception ex) => ex switch
    {
        RpcException rpc when !string.IsNullOrWhiteSpace(rpc.Status.Detail) =>
            $"{rpc.StatusCode}: {rpc.Status.Detail}",
        RpcException rpc => rpc.StatusCode.ToString(),
        _ => ex.Message
    };

    private void UpsertRoom(ChatRoom room)
    {
        var index = rooms.FindIndex(item => item.RoomId == room.RoomId);
        if (index >= 0)
        {
            rooms[index] = room;
        }
        else
        {
            rooms.Add(room);
        }
    }

    private void UpdateTyping(string userName, bool isTyping)
    {
        if (string.Equals(userName, UserName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (isTyping)
        {
            if (!typingUsers.Contains(userName, StringComparer.OrdinalIgnoreCase))
            {
                typingUsers.Add(userName);
            }
        }
        else
        {
            typingUsers.RemoveAll(item => string.Equals(item, userName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void AddEvent(string message)
    {
        lock (sync)
        {
            AddEventLocked(message);
        }
        logger.LogInformation("{Message}", message);
    }

    private void AddEventLocked(string message)
    {
        eventLog.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss} {message}");
        if (eventLog.Count > 40)
        {
            eventLog.RemoveRange(40, eventLog.Count - 40);
        }
    }

    private void NotifyChanged()
    {
        if (!disposed)
        {
            Changed?.Invoke();
        }
    }
}
