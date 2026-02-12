using Microsoft.AspNetCore.Builder;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;
using Yarp.ReverseProxy.Configuration;

Console.WriteLine(@"

 ██████╗ ███╗   ██╗      ██████╗ ██████╗ ███████╗███╗   ███╗     █████╗  ██████╗  ██████╗ 
██╔═══██╗████╗  ██║      ██╔══██╗██╔══██╗██╔════╝████╗ ████║    ██╔══██╗██╔════╝ ██╔════╝ 
██║   ██║██╔██╗ ██║█████╗██████╔╝██████╔╝█████╗  ██╔████╔██║    ███████║██║  ███╗██║  ███╗
██║   ██║██║╚██╗██║╚════╝██╔═══╝ ██╔══██╗██╔══╝  ██║╚██╔╝██║    ██╔══██║██║   ██║██║   ██║
╚██████╔╝██║ ╚████║      ██║     ██║  ██║███████╗██║ ╚═╝ ██║    ██║  ██║╚██████╔╝╚██████╔╝
 ╚═════╝ ╚═╝  ╚═══╝      ╚═╝     ╚═╝  ╚═╝╚══════╝╚═╝     ╚═╝    ╚═╝  ╚═╝ ╚═════╝  ╚═════╝ 
                                                                                          

    UFX.Relay Sample On-Prem Aggregate started
");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAggregatedTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
});

builder.Services.AddTunnelClient(options =>
    options with
    {
        TunnelHost = "wss://localhost:7400",
        TunnelId = "on-prem-aggregator",
    });

var downstreamAppBaseUrl = builder.Configuration["DownstreamApp:BaseUrl"] ?? "http://localhost:5600";
if (!downstreamAppBaseUrl.EndsWith('/'))
{
    downstreamAppBaseUrl += "/";
}

builder.Services.AddReverseProxy().LoadFromMemory(
    [
        new RouteConfig
        {
            RouteId = "internal-app-route",
            ClusterId = "internal-app-cluster",
            Match = new RouteMatch
            {
                Path = "/internal/{**catch-all}"
            },
            Transforms =
            [
                new Dictionary<string, string>
                {
                    ["PathRemovePrefix"] = "/internal"
                }
            ]
        }
    ],
    [
        new ClusterConfig
        {
            ClusterId = "internal-app-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["internal-app"] = new()
                {
                    Address = downstreamAppBaseUrl
                }
            }
        }
    ]);

var app = builder.Build();

app.MapGet("/gateway", () => "Hello from On-Prem gateway app");

app.MapReverseProxy();
app.MapTunnelForwarder();
await app.RunAsync();
