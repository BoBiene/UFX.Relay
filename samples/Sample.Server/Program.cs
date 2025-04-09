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

PathPrefixTransformer prefixTransformer = new PathPrefixTransformer("ufx");
builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
    options.TunnelIdFromContext = prefixTransformer.GetTunnelIdFromContext;

    options.Transformer = (TransformBuilderContext context) =>
    {
        // Remove /tunnel/{tunnelId} from the request path before forwarding
        context.RequestTransforms.Add(prefixTransformer);
        //context.RequestTransforms.Add(new RequestHeaderXForwardedPrefixTransform("X-Forwarded-Prefix", ForwardedTransformActions.Set));
    };
});
var app = builder.Build();
app.MapTunnelHost();
app.MapTunnelForwarder();
app.MapGet("/", () => builder.Environment.ApplicationName);
app.MapGet("/server", () => "Hello from Server");
await app.RunAsync();