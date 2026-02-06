using Microsoft.AspNetCore.Builder;
using UFX.Relay.Tunnel;
using UFX.Relay.Tunnel.Forwarder;

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
builder.Services.AddHttpClient("downstream-app", client => client.BaseAddress = new Uri(downstreamAppBaseUrl));

var app = builder.Build();

app.MapGet("/gateway", () => "Hello from On-Prem gateway app");

app.MapGet("/internal", async (IHttpClientFactory httpClientFactory) =>
{
    var httpClient = httpClientFactory.CreateClient("downstream-app");
    return await httpClient.GetStringAsync("/");
});

app.MapGet("/internal/{**path}", async (string? path, IHttpClientFactory httpClientFactory) =>
{
    var httpClient = httpClientFactory.CreateClient("downstream-app");
    return await httpClient.GetStringAsync(path ?? string.Empty);
});

app.MapTunnelForwarder();
await app.RunAsync();
