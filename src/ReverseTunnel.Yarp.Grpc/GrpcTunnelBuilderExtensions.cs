using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel;
using ReverseTunnel.Yarp.Tunnel.Registry;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Grpc;

public static class GrpcTunnelBuilderExtensions
{
    public static IServiceCollection AddReverseTunnelGrpcTransport(
        this IServiceCollection services,
        Action<ReverseTunnelGrpcTransportOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<ReverseTunnelGrpcTransportOptions>();
        }

        services.AddGrpc();
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<ReverseTunnelGrpcTransportOptions>>((kestrelOptions, grpcOptions) =>
            {
                kestrelOptions.Limits.Http2.KeepAlivePingDelay = grpcOptions.Value.ServerKeepAlivePingDelay;
                kestrelOptions.Limits.Http2.KeepAlivePingTimeout = grpcOptions.Value.ServerKeepAlivePingTimeout;
            });
        services.TryAddSingleton<WebSocketTunnelClientTransport>();
        services.AddSingleton<GrpcTunnelClientTransport>();
        services.Replace(ServiceDescriptor.Singleton<ITunnelClientTransport>(provider =>
            new ConfiguredTunnelClientTransport(
                provider.GetRequiredService<ITunnelClientOptionsStore>(),
                new ITunnelClientTransport[]
                {
                    provider.GetRequiredService<WebSocketTunnelClientTransport>(),
                    provider.GetRequiredService<GrpcTunnelClientTransport>()
                })));
        services.TryAddSingleton<ITunnelRegistry, InMemoryTunnelRegistry>();
        services.TryAddSingleton<ReverseTunnelInstanceInfo>();
        services.AddOptions<ReverseTunnelOptions>();
        return services;
    }

    public static IEndpointConventionBuilder MapReverseTunnelGrpcTransport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGrpcService<TunnelTransportService>();
}