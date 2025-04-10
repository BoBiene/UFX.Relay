using Microsoft.AspNetCore.Builder;
using UFX.Relay.Tunnel;
using UFX.Relay.Tunnel.Forwarder;


Console.WriteLine(@"

 ██████╗██╗      ██████╗ ██╗   ██╗██████╗      █████╗  ██████╗  ██████╗ 
██╔════╝██║     ██╔═══██╗██║   ██║██╔══██╗    ██╔══██╗██╔════╝ ██╔════╝ 
██║     ██║     ██║   ██║██║   ██║██║  ██║    ███████║██║  ███╗██║  ███╗
██║     ██║     ██║   ██║██║   ██║██║  ██║    ██╔══██║██║   ██║██║   ██║
╚██████╗███████╗╚██████╔╝╚██████╔╝██████╔╝    ██║  ██║╚██████╔╝╚██████╔╝
 ╚═════╝╚══════╝ ╚═════╝  ╚═════╝ ╚═════╝     ╚═╝  ╚═╝ ╚═════╝  ╚═════╝ 
                                                                        

    UFX.Relay Sample Cloud Aggregate started
");


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTunnelForwarder(options =>
{
    options.DefaultTunnelId = "123";
});
var app = builder.Build();
app.MapTunnelHost();
await app.RunAsync();