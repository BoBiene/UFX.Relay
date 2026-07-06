using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;

Console.WriteLine("ReverseTunnel.Yarp Sample Client started");

var builder = WebApplication.CreateBuilder(args);
var tunnelSection = builder.Configuration.GetSection("ReverseTunnel");
var tunnelTransport = GetTransport(tunnelSection["Transport"]);

builder.WebHost.AddTunnelListener(options =>
    tunnelSection.GetSection("Listener").Bind(options),
    includeDefaultUrls: true);
builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = tunnelSection["TunnelHost"] ?? "wss://localhost:7200",
        TunnelId = tunnelSection["TunnelId"] ?? "123",
        TunnelPathTemplate = tunnelSection["TunnelPathTemplate"] ?? options.TunnelPathTemplate,
        Transport = tunnelTransport,
        RequestHeaders = tunnelSection.GetSection("RequestHeaders")
            .GetChildren()
            .ToDictionary(header => header.Key, header => header.Value ?? string.Empty)
    });
builder.Services.AddReverseTunnelGrpcTransport(options =>
    builder.Configuration.GetSection("ReverseTunnel:Grpc").Bind(options));

var app = builder.Build();

// Middleware to demonstrate tunnel request detection.
app.Use(async (context, next) =>
{
    if (context.IsFromTunnel())
    {
        // Request came through the tunnel - trusted connection.
        Console.WriteLine($"[TUNNEL] Request to {context.Request.Path} from tunnel");

        // Example: Trust x-User header only from tunnel.
        var user = context.Request.Headers["x-User"].ToString();
        if (!string.IsNullOrEmpty(user))
        {
            Console.WriteLine($"[TUNNEL] Authenticated user from header: {user}");
            context.Items["AuthenticatedUser"] = user;
        }
    }
    else
    {
        // Request came through normal HTTP endpoint.
        Console.WriteLine($"[HTTP] Request to {context.Request.Path} from normal HTTP");

        // Remove x-User header from untrusted sources.
        context.Request.Headers.Remove("x-User");
    }

    await next(context);
});

app.MapGet("/", () => builder.Environment.ApplicationName);
app.MapGet("/client", () => "Hello from Client");
app.MapGet("/transport", () => new
{
    TunnelHost = tunnelSection["TunnelHost"] ?? "wss://localhost:7200",
    TunnelId = tunnelSection["TunnelId"] ?? "123",
    Transport = tunnelTransport.ToString()
});
app.MapGet("/auth-test", (HttpContext context) =>
{
    var user = context.Items["AuthenticatedUser"]?.ToString();
    var isFromTunnel = context.IsFromTunnel();
    return new
    {
        IsFromTunnel = isFromTunnel,
        AuthenticatedUser = user ?? "Not authenticated",
        Message = isFromTunnel
            ? "Request came through tunnel (trusted)"
            : "Request came through HTTP (untrusted)"
    };
});
await app.RunAsync();

static TunnelTransportKind GetTransport(string? value) =>
    Enum.TryParse<TunnelTransportKind>(value, ignoreCase: true, out var transport)
        ? transport
        : TunnelTransportKind.WebSocket;
