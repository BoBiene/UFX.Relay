using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel.Registry;
using ReverseTunnel.Yarp.Tunnel.Transport;
using Yarp.ReverseProxy.Transforms;

namespace ReverseTunnel.Yarp.Tunnel;

public static class TunnelBuilderExtensions
{
    public static IServiceCollection AddTunnelClient(this IServiceCollection services, string host) =>
      services.AddTunnelClient(options => options with { TunnelHost = host });

    public static IServiceCollection AddTunnelClient(this IServiceCollection services, TunnelClientOptionsUpdateHandler? clientOptions = null)
    {
        services.TryAddSingleton<ITunnelClientOptionsStore>(provider =>
        {
            var options = clientOptions is null ? new() : clientOptions(new());
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
        services.TryAddSingleton<WebSocketTunnelClientTransport>();
        services.TryAddSingleton<ITunnelClientTransport>(provider => provider.GetRequiredService<WebSocketTunnelClientTransport>());
        services.TryAddTunnelClientManager();
        services.TryAddSingleton<ITunnelRegistry, InMemoryTunnelRegistry>();
        services.TryAddSingleton<ReverseTunnelInstanceInfo>();
        services.AddOptions<ReverseTunnelOptions>();

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
            await tunnelManager.StartTunnelAsync(context, tunnelId);
            if (context.Response.StatusCode == StatusCodes.Status400BadRequest) return Results.BadRequest();
            return Results.Empty;
        }).ExcludeFromDescription();
    }
}