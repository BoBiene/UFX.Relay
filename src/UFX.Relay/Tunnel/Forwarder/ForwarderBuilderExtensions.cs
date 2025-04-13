using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel.Forwarder;

public static class ForwarderBuilderExtensions
{
    public static IEndpointConventionBuilder MapTunnelForwarder(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = "{**catch-all}")
    {
        var pipeline = endpoints.CreateApplicationBuilder()
            .UseMiddleware<TunnelForwarderMiddleware>()
            .Build();
        return endpoints.Map(path, pipeline);
    }

    public static IServiceCollection AddTunnelForwarder(this IServiceCollection services, Action<TunnelForwarderOptions>? forwarderOptions = null)
    {
        services.AddTunnelForwarderInternal(forwarderOptions);
        services.TryAddSingleton<ITunnelClientFactory, HostTunnelClientFactory>();
        services.TryAddSingleton<ITunnelHostManager, TunnelHostManager>();
        return services;
    }

    public static IServiceCollection AddAggregatedTunnelForwarder(this IServiceCollection services, Action<TunnelForwarderOptions>? forwarderOptions = null)
    {
        services.AddTunnelForwarderInternal(forwarderOptions);
        services.TryAddSingleton<ITunnelHostManager, TunnelHostAggregatedManager>();
        return services;
    }

    private static IServiceCollection AddTunnelForwarderInternal(this IServiceCollection services, Action<TunnelForwarderOptions>? forwarderOptions = null)
    {
        if (forwarderOptions != null) services.Configure(forwarderOptions);
        services.AddHttpForwarder();
        services.AddHttpContextAccessor();

        services.AddSingleton<ITunnelIdProvider, ForwarderTunnelIdProvider>();
        services.AddSingleton<TunnelForwarderHttpClientFactory>();
        services.AddSingleton<TunnelForwarderMiddleware>();
        services.TryAddSingleton<ITunnelCollectionProvider, TunnelCollection>();

        return services;
    }

}