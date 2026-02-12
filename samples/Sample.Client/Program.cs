
using UFX.Relay.Tunnel;
using UFX.Relay.Tunnel.Listener;

Console.WriteLine(@"

 ██████╗██╗     ██╗███████╗███╗   ██╗████████╗
██╔════╝██║     ██║██╔════╝████╗  ██║╚══██╔══╝
██║     ██║     ██║█████╗  ██╔██╗ ██║   ██║   
██║     ██║     ██║██╔══╝  ██║╚██╗██║   ██║   
╚██████╗███████╗██║███████╗██║ ╚████║   ██║   
 ╚═════╝╚══════╝╚═╝╚══════╝╚═╝  ╚═══╝   ╚═╝   
                                              

    UFX.Relay Sample Client started
");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.AddTunnelListener(includeDefaultUrls: true);
builder.Services.AddTunnelClient(options =>
    options with
    {
        // Note: Use wss:// for secure production connections. 
        // This requires running with the 'https' profile in launchSettings.json
        TunnelHost = "wss://localhost:7200",
        TunnelId = "123"
    });
var app = builder.Build();

// Middleware to demonstrate tunnel request detection
app.Use(async (context, next) =>
{
    if (context.IsFromTunnel())
    {
        // Request came through the tunnel - trusted connection
        Console.WriteLine($"[TUNNEL] Request to {context.Request.Path} from tunnel");
        
        // Example: Trust x-User header only from tunnel
        var user = context.Request.Headers["x-User"].ToString();
        if (!string.IsNullOrEmpty(user))
        {
            Console.WriteLine($"[TUNNEL] Authenticated user from header: {user}");
            context.Items["AuthenticatedUser"] = user;
        }
    }
    else
    {
        // Request came through normal HTTP endpoint
        Console.WriteLine($"[HTTP] Request to {context.Request.Path} from normal HTTP");
        
        // Remove x-User header from untrusted sources
        context.Request.Headers.Remove("x-User");
    }
    
    await next(context);
});

app.MapGet("/", () => builder.Environment.ApplicationName);
app.MapGet("/client", () => "Hello from Client");
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