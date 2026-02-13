using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.AddTunnelListener(includeDefaultUrls: true);
builder.Services.AddTunnelClient("wss://demo.relay.ws");
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
var app = builder.Build();
app.MapReverseProxy();
app.Run();