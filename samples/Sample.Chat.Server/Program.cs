using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;
using Sample.Chat.Server.Services;

Console.WriteLine("Sample.Chat.Server started");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Services.Configure<ReverseTunnelOptions>(builder.Configuration.GetSection("ReverseTunnel"));
builder.Services.AddGrpc();
builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = builder.Configuration["ReverseTunnel:TunnelId"] ?? "chat-client";
});
builder.Services.AddReverseTunnelGrpcTransport(options =>
    builder.Configuration.GetSection("ReverseTunnel:Grpc").Bind(options));
builder.Services.AddSingleton<ChatState>();

var app = builder.Build();

app.MapReverseTunnelGrpcTransport();
app.MapGrpcService<ChatRoomServiceImpl>();
app.MapGrpcService<ChatMessageServiceImpl>();
app.MapGrpcService<ChatStreamServiceImpl>();

app.MapGet("/server-info", (IOptions<ReverseTunnelOptions> options) => new
{
    Name = "Sample.Chat.Server",
    Time = DateTimeOffset.UtcNow,
    options.Value.InstanceId,
    options.Value.InternalEndpoint
});

app.MapTunnelForwarder();
await app.RunAsync();