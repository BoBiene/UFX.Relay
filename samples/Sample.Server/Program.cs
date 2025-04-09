using System.Text.RegularExpressions;
using UFX.Relay.Tunnel;
using UFX.Relay.Tunnel.Forwarder;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;




Console.WriteLine(@"

███████╗███████╗██████╗ ██╗   ██╗███████╗██████╗ 
██╔════╝██╔════╝██╔══██╗██║   ██║██╔════╝██╔══██╗
███████╗█████╗  ██████╔╝██║   ██║█████╗  ██████╔╝
╚════██║██╔══╝  ██╔══██╗╚██╗ ██╔╝██╔══╝  ██╔══██╗
███████║███████╗██║  ██║ ╚████╔╝ ███████╗██║  ██║
╚══════╝╚══════╝╚═╝  ╚═╝  ╚═══╝  ╚══════╝╚═╝  ╚═╝
                                                 

    UFX.Relay Sample Server gestartet
");


var builder = WebApplication.CreateBuilder(args);

TunnelPathPrefixTransformer prefixTransformer = new("ufx");
builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
    options.TunnelIdFromContext = prefixTransformer.GetTunnelIdFromContext;
    options.Transformer = context =>
    {
        // Remove /ufx/{tunnelId} from the request path before forwarding
        context.RequestTransforms.Add(prefixTransformer);
    };
});
var app = builder.Build();
app.MapTunnelHost();
app.MapTunnelForwarder();
app.MapGet("/", () => builder.Environment.ApplicationName);
app.MapGet("/server", () => "Hello from Server");
await app.RunAsync();