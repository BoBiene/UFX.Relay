using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;

Console.WriteLine("ReverseTunnel.Yarp Sample Server started");

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ReverseTunnelOptions>(builder.Configuration.GetSection("ReverseTunnel"));

TunnelPathPrefixTransformer prefixTransformer = new("arty");
builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
    options.TunnelIdFromContext = prefixTransformer.GetTunnelIdFromContext;
    options.Transformer = context =>
    {
        // Remove /arty/{tunnelId} from the request path before forwarding.
        context.RequestTransforms.Add(prefixTransformer);
    };
});
builder.Services.AddReverseTunnelGrpcTransport(options =>
    builder.Configuration.GetSection("ReverseTunnel:Grpc").Bind(options));

var app = builder.Build();
app.MapTunnelHost();
app.MapReverseTunnelGrpcTransport();
app.MapTunnelForwarder();
app.MapGet("/", () => builder.Environment.ApplicationName);
app.MapGet("/server", () => "Hello from Server");
app.MapGet("/transport", (IOptions<ReverseTunnelOptions> options) => new
{
    options.Value.Transport,
    options.Value.InstanceId,
    options.Value.InternalEndpoint
});
await app.RunAsync();
