
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
builder.WebHost.AddTunnelListener(options =>
    options with
    {
        DefaultTunnelId = "123"
    });
builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7200",
        TunnelId = "123"
    });
var app = builder.Build();

app.MapGet("/", () => builder.Environment.ApplicationName);
app.MapGet("/client", () => "Hello from Client");
await app.RunAsync();