using Microsoft.AspNetCore.Builder;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Forwarder;


Console.WriteLine(@"

 ██████╗██╗      ██████╗ ██╗   ██╗██████╗      █████╗  ██████╗  ██████╗ 
██╔════╝██║     ██╔═══██╗██║   ██║██╔══██╗    ██╔══██╗██╔════╝ ██╔════╝ 
██║     ██║     ██║   ██║██║   ██║██║  ██║    ███████║██║  ███╗██║  ███╗
██║     ██║     ██║   ██║██║   ██║██║  ██║    ██╔══██║██║   ██║██║   ██║
╚██████╗███████╗╚██████╔╝╚██████╔╝██████╔╝    ██║  ██║╚██████╔╝╚██████╔╝
 ╚═════╝╚══════╝ ╚═════╝  ╚═════╝ ╚═════╝     ╚═╝  ╚═╝ ╚═════╝  ╚═════╝ 
                                                                        

    ReverseTunnel.Yarp Sample Cloud Aggregate started
");


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTunnelForwarder();
var app = builder.Build();
app.MapTunnelHost();
await app.RunAsync();