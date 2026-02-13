using Microsoft.AspNetCore.HttpOverrides;
using Sample.Blazor.Components;
using Sample.Blazor.Gateway;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;
using Yarp.ReverseProxy.Forwarder;

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
                                                                                                  
                                                 
   ReverseTunnel.Yarp Sample Blazor Client gestartet
");


            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddReverseProxy();
            builder.Services.AddSingleton<GatewayRouteStore>();
            builder.Services.AddSingleton<HttpMessageInvoker>(_ =>
            {
                var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                    UseCookies = false,
                    EnableMultipleHttp2Connections = true,
                    ActivityHeadersPropagator = null
                };

                return new HttpMessageInvoker(handler);
            });
            builder.Services.AddSingleton<ForwarderRequestConfig>(_ => new ForwarderRequestConfig
            {
                ActivityTimeout = TimeSpan.FromMinutes(2)
            });

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

            app.MapGet("/gateway/routes", (GatewayRouteStore store) => Results.Ok(store.GetAll()));

            app.Map("/gateway/{**catch-all}", async (HttpContext context, GatewayRouteStore store, IHttpForwarder forwarder, HttpMessageInvoker httpClient, ForwarderRequestConfig requestConfig) =>
            {
                var gatewayPrefix = "/gateway";
                var requestPath = context.Request.Path.Value ?? "/";
                var relativePath = requestPath.Length > gatewayPrefix.Length
                    ? requestPath[gatewayPrefix.Length..]
                    : "/";

                if (!store.TryMatch(relativePath, out var route, out var rewrittenPath))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync($"No matching route configured for '{relativePath}'.");
                    return;
                }

                var destinationPrefix = route.DestinationBaseUrl.TrimEnd('/'); // e.g. "http://localhost:5600"

                var transformer = new PathOverrideTransformer(rewrittenPath);

                var error = await forwarder.SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer);

                if (error == ForwarderError.None)
                {
                    return;
                }

                var errorFeature = context.GetForwarderErrorFeature();
                var errorException = errorFeature?.Exception;
                var correlationId = Guid.NewGuid().ToString("N");
                await Console.Error.WriteLineAsync($"Proxy error (CorrelationId: {correlationId}): {error}. Exception: {errorException}");
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync($"Proxy error. Please contact support with CorrelationId: {correlationId}.");

                await app.RunAsync();

            });
        }
    }
}
