using Microsoft.AspNetCore.HttpOverrides;
using Sample.Blazor.Components;
using UFX.Relay.Tunnel;
using UFX.Relay.Tunnel.Listener;

namespace Sample.Blazor
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine(@"

██████╗  █████╗ ██╗     ███████╗ ██████╗ ██████╗ 
██╔══██╗██╔══██╗██║     ╚══███╔╝██╔═══██╗██╔══██╗
██████╔╝███████║██║       ███╔╝ ██║   ██║██████╔╝
██╔══██╗██╔══██║██║      ███╔╝  ██║   ██║██╔══██╗
██████╔╝██║  ██║███████╗███████╗╚██████╔╝██║  ██║
╚═════╝ ╚═╝  ╚═╝╚══════╝╚══════╝ ╚═════╝ ╚═╝  ╚═╝
                                                                                                  
                                                 
   UFX.Relay Sample Blazor Client gestartet
");


            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.WebHost.AddTunnelListener(includeDefaultUrls: true);
            builder.Services.AddTunnelClient(options =>
                options with
                {
                    TunnelHost = "wss://localhost:7200",
                    TunnelId = "BlazorSample",
                    IsEnabled = false
                });

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            // !-- IMPORTANT: This must be done to ensure that the app works behind our reverse proxy (tunnel) --
            // Enable Forwarded Headers for reverse proxy scenarios
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.All,
                // Optional:
                // Add KnownProxies / KnownNetworks for more secure forwarding
            });


            app.UseHttpsRedirection();

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            await app.RunAsync();
        }
    }
}
