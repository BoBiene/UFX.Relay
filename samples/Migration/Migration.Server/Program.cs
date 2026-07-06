using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;

// Migration server: exposes the WebSocket AND the gRPC tunnel transport at the same time.
// A fleet of clients can be migrated one client at a time - legacy WebSocket clients keep
// working while new clients switch to gRPC, all against this single server.
Console.WriteLine("Migration.Server started (WebSocket + gRPC tunnel transports enabled)");

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ReverseTunnelOptions>(builder.Configuration.GetSection("ReverseTunnel"));

// Requests are routed to a tunnel by the "/tunnel/{tunnelId}/..." path prefix, so the same
// server can forward to the "legacy" tunnel and the "modern" tunnel independently.
var prefixTransformer = new TunnelPathPrefixTransformer("tunnel");
builder.Services.AddTunnelForwarder(options =>
{
    options.TunnelIdFromContext = prefixTransformer.GetTunnelIdFromContext;
    options.Transformer = context => context.RequestTransforms.Add(prefixTransformer);
});

// Enabling the gRPC transport is purely additive; the WebSocket transport keeps working.
builder.Services.AddReverseTunnelGrpcTransport(options =>
    builder.Configuration.GetSection("ReverseTunnel:Grpc").Bind(options));

var app = builder.Build();

// Both transport endpoints are mapped side by side on the same host/port.
app.MapTunnelHost();                    // WebSocket transport  (legacy clients)
app.MapReverseTunnelGrpcTransport();    // gRPC transport       (modern clients)

app.MapGet("/", () => Results.Content("""
    Migration.Server

    This server offers the WebSocket and the gRPC tunnel transport simultaneously.

    Reach the legacy (WebSocket) client through the tunnel:
      GET /tunnel/legacy/hello

    Reach the modern (gRPC) client through the tunnel:
      GET /tunnel/modern/hello

    Transport info:
      GET /transports
    """, "text/plain"));

app.MapGet("/transports", (IOptions<ReverseTunnelOptions> options) => new
{
    WebSocket = "enabled (MapTunnelHost)",
    Grpc = "enabled (MapReverseTunnelGrpcTransport)",
    options.Value.InstanceId,
    options.Value.InternalEndpoint,
    Note = "Legacy WebSocket clients and modern gRPC clients connect to this same server."
});

// Catch-all forwarder: forwards "/tunnel/{tunnelId}/..." to the owning client tunnel,
// regardless of whether that client connected over WebSocket or gRPC.
app.MapTunnelForwarder();

await app.RunAsync();
