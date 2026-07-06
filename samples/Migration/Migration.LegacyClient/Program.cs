using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;

// Legacy client: connects over the WebSocket tunnel transport, exactly like before the
// gRPC transport existed. It does not reference ReverseTunnel.Yarp.Grpc at all.
Console.WriteLine("Migration.LegacyClient started (WebSocket transport)");

var builder = WebApplication.CreateBuilder(args);
var tunnelSection = builder.Configuration.GetSection("ReverseTunnel");

builder.WebHost.AddTunnelListener(options =>
    tunnelSection.GetSection("Listener").Bind(options),
    includeDefaultUrls: true);

builder.Services.AddTunnelClient(options => options with
{
    TunnelHost = tunnelSection["TunnelHost"] ?? "wss://localhost:7400",
    TunnelId = tunnelSection["TunnelId"] ?? "legacy",
    // No Transport is set -> defaults to WebSocket, matching an old client in the field.
});

var app = builder.Build();

app.MapGet("/hello", () => new
{
    Client = "Migration.LegacyClient",
    Transport = "WebSocket",
    Time = DateTimeOffset.UtcNow
});

await app.RunAsync();
