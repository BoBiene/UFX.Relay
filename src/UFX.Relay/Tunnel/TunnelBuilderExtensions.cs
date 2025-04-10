using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using UFX.Relay.Abstractions;
using Yarp.ReverseProxy.Transforms;

namespace UFX.Relay.Tunnel;

public static class TunnelBuilderExtensions
{
    public static IServiceCollection AddTunnelClient(this IServiceCollection services, string host) =>
      services.AddTunnelClient(options => options.TunnelHost = host);

    public static IServiceCollection AddTunnelClient(this IServiceCollection services, Action<TunnelClientOptions>? clientOptions = null)
    {
        if (clientOptions != null)
            services.Configure(clientOptions);
        else
            services.Configure<TunnelClientOptions>(_ => { }); // Empty default config


        services.TryAddSingleton<ITunnelClientOptionsStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TunnelClientOptions>>().Value;
            return new TunnelClientOptionsStore(options);
        });

        return services.AddTunnelClientInternal();
    }

    public static IServiceCollection AddTunnelClient(this IServiceCollection services, ITunnelClientOptionsStore tunnelClientOptionsStore)
    {
        services.TryAddSingleton(tunnelClientOptionsStore);
        return services.AddTunnelClientInternal();
    }

    private static IServiceCollection AddTunnelClientInternal(this IServiceCollection services)
    {
        services.TryAddSingleton<ITunnelClientFactory, ClientTunnelClientFactory>();
        services.TryAddSingleton<ITunnelClientManager, TunnelClientManager>();

        return services;
    }

    public static IEndpointConventionBuilder MapTunnelHost(this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string path = "/tunnel/{tunnelId}",
        Action<WebSocketOptions>? webSocketOptions = null)
    {
        IApplicationBuilder app = endpoints as IApplicationBuilder ?? throw new ArgumentNullException(nameof(endpoints));
        var options = new WebSocketOptions();
        webSocketOptions?.Invoke(options);
        app.UseWebSockets(options);
        return endpoints.MapGet(path, static async (HttpContext context, string tunnelId, ITunnelHostManager tunnelManager) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            await tunnelManager.StartTunnelAsync(context, tunnelId);
            return Results.Empty;
        }).ExcludeFromDescription();
    }
}