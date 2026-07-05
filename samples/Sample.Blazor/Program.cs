using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using ReverseTunnel.Yarp.Grpc;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;
using Sample.Blazor.Components;
using Sample.Blazor.Gateway;
using Yarp.ReverseProxy.Forwarder;

namespace Sample.Blazor
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine("ReverseTunnel.Yarp Sample Blazor Client started");

            var builder = WebApplication.CreateBuilder(args);
            if (builder.Environment.IsEnvironment("Grpc"))
            {
                builder.WebHost.UseStaticWebAssets();
            }
            var tunnelSection = builder.Configuration.GetSection("ReverseTunnel");
            var tunnelTransport = GetTransport(tunnelSection["Transport"]);

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

            builder.WebHost.AddTunnelListener(options =>
                tunnelSection.GetSection("Listener").Bind(options),
                includeDefaultUrls: true);
            builder.Services.AddTunnelClient(options =>
                options with
                {
                    TunnelHost = tunnelSection["TunnelHost"] ?? "wss://localhost:7200",
                    TunnelId = tunnelSection["TunnelId"] ?? "BlazorSample",
                    TunnelPathTemplate = tunnelSection["TunnelPathTemplate"] ?? options.TunnelPathTemplate,
                    Transport = tunnelTransport,
                    IsEnabled = tunnelSection.GetValue<bool?>("IsEnabled") ?? false,
                    RequestHeaders = tunnelSection.GetSection("RequestHeaders")
                        .GetChildren()
                        .ToDictionary(header => header.Key, header => header.Value ?? string.Empty)
                });
            builder.Services.AddReverseTunnelGrpcTransport(options =>
                builder.Configuration.GetSection("ReverseTunnel:Grpc").Bind(options));

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.All
            });

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.MapGet("/gateway/routes", (GatewayRouteStore store) => Results.Ok(store.GetAll()));

            app.Map("/gateway/{**catch-all}", async (
                HttpContext context,
                GatewayRouteStore store,
                IHttpForwarder forwarder,
                HttpMessageInvoker httpClient,
                ForwarderRequestConfig requestConfig) =>
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

                var destinationPrefix = route.DestinationBaseUrl.TrimEnd('/');
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
            });

            await app.RunAsync();
        }

        private static TunnelTransportKind GetTransport(string? value) =>
            Enum.TryParse<TunnelTransportKind>(value, ignoreCase: true, out var transport)
                ? transport
                : TunnelTransportKind.WebSocket;
    }
}
