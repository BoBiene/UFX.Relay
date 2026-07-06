using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;

// Modern client: connects to the same server, but over the gRPC tunnel transport.
Console.WriteLine("Migration.ModernClient started (gRPC transport)");

var builder = WebApplication.CreateBuilder(args);
var tunnelSection = builder.Configuration.GetSection("ReverseTunnel");

builder.WebHost.AddTunnelListener(options =>
    tunnelSection.GetSection("Listener").Bind(options),
    includeDefaultUrls: true);

builder.Services.AddTunnelClient(options => options with
{
    TunnelHost = tunnelSection["TunnelHost"] ?? "https://localhost:7400",
    TunnelId = tunnelSection["TunnelId"] ?? "modern",
    Transport = TunnelTransportKind.Grpc,
});

// Registering the gRPC transport makes TunnelTransportKind.Grpc available to the client.
builder.Services.AddReverseTunnelGrpcTransport(options =>
    builder.Configuration.GetSection("ReverseTunnel:Grpc").Bind(options));

var app = builder.Build();

app.MapGet("/hello", () => new
{
    Client = "Migration.ModernClient",
    Transport = "Grpc",
    Time = DateTimeOffset.UtcNow
});

await app.RunAsync();
