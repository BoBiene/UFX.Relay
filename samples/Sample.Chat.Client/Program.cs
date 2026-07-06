using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;
using Sample.Chat.Client.Components;
using Sample.Chat.Client.Services;
using Sample.Chat.Shared;

Console.WriteLine("Sample.Chat.Client started");

var builder = WebApplication.CreateBuilder(args);
var serverUrl = builder.Configuration["ChatServer:Url"] ?? "https://localhost:7300";
var tunnelSection = builder.Configuration.GetSection("ReverseTunnel");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<ChatClientResilienceOptions>(
    builder.Configuration.GetSection("ChatClient:Resilience"));

builder.Services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("SharedGrpcChannel");
    logger.LogInformation("Shared gRPC channel created for {ServerUrl}", serverUrl);
    return GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        },
        MaxRetryAttempts = 3,
        // Only safe read calls are retried automatically; writes need idempotency.
        ServiceConfig = new ServiceConfig
        {
            MethodConfigs =
            {
                CreateSafeReadRetry("sample.chat.ChatRoomService", "ListRooms"),
                CreateSafeReadRetry("sample.chat.ChatMessageService", "GetRecentMessages")
            }
        }
    });
});
builder.Services.AddSingleton(provider =>
    new ChatRoomService.ChatRoomServiceClient(provider.GetRequiredService<GrpcChannel>()));
builder.Services.AddSingleton(provider =>
    new ChatMessageService.ChatMessageServiceClient(provider.GetRequiredService<GrpcChannel>()));
builder.Services.AddSingleton(provider =>
    new ChatStreamService.ChatStreamServiceClient(provider.GetRequiredService<GrpcChannel>()));
builder.Services.AddHttpClient("TunnelTest", client =>
{
    client.BaseAddress = new Uri(serverUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<ChatClientSession>();

builder.WebHost.AddTunnelListener(options =>
    tunnelSection.GetSection("Listener").Bind(options),
    includeDefaultUrls: true);
builder.Services.AddTunnelClient(options => options with
{
    TunnelHost = tunnelSection["TunnelHost"] ?? serverUrl,
    TunnelId = tunnelSection["TunnelId"] ?? "chat-client",
    TunnelPathTemplate = tunnelSection["TunnelPathTemplate"] ?? options.TunnelPathTemplate,
    Transport = GetTransport(tunnelSection["Transport"]),
    IsEnabled = tunnelSection.GetValue<bool?>("IsEnabled") ?? true,
    RequestHeaders = tunnelSection.GetSection("RequestHeaders")
        .GetChildren()
        .ToDictionary(header => header.Key, header => header.Value ?? string.Empty)
});
builder.Services.AddReverseTunnelGrpcTransport(options =>
{
    builder.Configuration.GetSection("ReverseTunnel:Grpc").Bind(options);
    options.CallInvokerFactory = provider =>
        provider.GetRequiredService<GrpcChannel>().CreateCallInvoker();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/client-info", (GrpcChannel channel, ITunnelClientManager tunnelManager) => new
{
    Name = "Sample.Chat.Client",
    Time = DateTimeOffset.UtcNow,
    SharedChannelHashCode = channel.GetHashCode(),
    TunnelState = tunnelManager.ConnectionState.ToString(),
    UsesSharedChannelFor = new[]
    {
        "TunnelTransport.Connect",
        "ChatRoomService.CreateRoom",
        "ChatRoomService.ListRooms",
        "ChatMessageService.SendMessage",
        "ChatStreamService.Connect"
    }
});

await app.RunAsync();

static TunnelTransportKind GetTransport(string? value) =>
    Enum.TryParse<TunnelTransportKind>(value, ignoreCase: true, out var transport)
        ? transport
        : TunnelTransportKind.WebSocket;

static MethodConfig CreateSafeReadRetry(string service, string method) => new()
{
    Names = { new MethodName { Service = service, Method = method } },
    RetryPolicy = new RetryPolicy
    {
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromMilliseconds(500),
        MaxBackoff = TimeSpan.FromSeconds(5),
        BackoffMultiplier = 2,
        RetryableStatusCodes =
        {
            StatusCode.Unavailable,
            StatusCode.DeadlineExceeded
        }
    }
};
