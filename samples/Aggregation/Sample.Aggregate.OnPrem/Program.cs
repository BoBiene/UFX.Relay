﻿
using Microsoft.AspNetCore.Builder;
using UFX.Relay.Tunnel;
using UFX.Relay.Tunnel.Forwarder;
using UFX.Relay.Tunnel.Listener;

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
{
    options.TunnelHost = "wss://localhost:7400";
    options.TunnelId = "on-prem-aggregator";
});
var app = builder.Build();
app.MapTunnelForwarder();
await app.RunAsync();