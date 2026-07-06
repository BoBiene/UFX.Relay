using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel.Listener;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel;

internal static class TunnelClientManagerServiceCollectionExtensions
{
    public static IServiceCollection TryAddTunnelClientManager(this IServiceCollection services)
    {
        services.TryAddSingleton<ITunnelClientManager>(CreateTunnelClientManager);
        return services;
    }

    private static TunnelClientManager CreateTunnelClientManager(IServiceProvider provider) =>
        new(
            provider.GetRequiredService<ITunnelClientOptionsStore>(),
            provider.GetRequiredService<IOptions<TunnelListenerOptions>>(),
            provider.GetRequiredService<ITunnelClientTransport>(),
            provider.GetRequiredService<ILogger<TunnelClientManager>>());
}
