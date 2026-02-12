using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;


Console.WriteLine(@"

███████╗███████╗██████╗ ██╗   ██╗███████╗██████╗ 
██╔════╝██╔════╝██╔══██╗██║   ██║██╔════╝██╔══██╗
███████╗█████╗  ██████╔╝██║   ██║█████╗  ██████╔╝
╚════██║██╔══╝  ██╔══██╗╚██╗ ██╔╝██╔══╝  ██╔══██╗
███████║███████╗██║  ██║ ╚████╔╝ ███████╗██║  ██║
╚══════╝╚══════╝╚═╝  ╚═╝  ╚═══╝  ╚══════╝╚═╝  ╚═╝
                                                 

    UFX.Relay Sample Server started
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