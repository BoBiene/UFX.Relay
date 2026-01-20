using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel.Listener;

public static class ListenerBuilderExtensions
{
    private static bool tunnelListenerAdded;
    private static ILogger logger = LoggerFactory.Create(logBuilder => logBuilder.AddConsole()).CreateLogger(typeof(ListenerBuilderExtensions));
    public static IWebHostBuilder AddTunnelListener(this IWebHostBuilder builder, TunnelListenerOptionsUpdateHandler? tunnelOptions = null, bool includeDefaultUrls = false)
    {
        if (tunnelListenerAdded)
        {
            logger.LogWarning("Tunnel Listener already added");
            return builder;
        }
        tunnelListenerAdded = true;
        builder.ConfigureKestrel(options =>
        {
            options.ListenOnTunnel();
            var urls = builder.GetSetting(WebHostDefaults.ServerUrlsKey)?.Split(';', StringSplitOptions.TrimEntries);
            if (includeDefaultUrls && urls is { Length: > 0 }) options.ListenOnUrls(urls);
        });
        builder.ConfigureServices(services =>
        {
            services.AddTunnelListener(tunnelOptions);
        });
        return builder;
    }

    private static IServiceCollection AddTunnelListener(this IServiceCollection services, TunnelListenerOptionsUpdateHandler? tunnelOptions = null)
    {
        services.TryAddSingleton<ITunnelListenerOptionsStore>(provider =>
        {
            var options = tunnelOptions is null ? new() : tunnelOptions(new());
            return new TunnelListenerOptionsStore(options);
        });

        services.TryAddSingleton<ITunnelIdProvider, ListenerTunnelIdProvider>();
        services.TryAddSingleton<ITunnelClientManager, TunnelClientManager>();
        services.TryAddSingleton<SocketTransportFactory>();
        services.TryAddSingleton<TunnelConnectionListenerFactory>();
        services.AddSingleton<IConnectionListenerFactory, TunnelCompositeTransportFactory>();
        return services;
    }

    private static KestrelServerOptions ListenOnTunnel(this KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Listen(new TunnelEndpoint());
        logger.LogInformation("Added listener endpoint: tunnel://");
        return options;
    }

    private static KestrelServerOptions ListenOnUrls(this KestrelServerOptions options, params string[] urls)
    {
        foreach (var url in urls)
        {
            var uri = new Uri(url);
            if (IPEndPoint.TryParse(uri.Host, out var endpoint))
            {
                options.Listen(endpoint, HandleOptions(uri));
                logger.LogInformation("Added listener endpoint: '{Uri}' via ConfigureKestrel()", uri);
                continue;
            }
            if (uri.Host != "localhost") continue;
            options.ListenAnyIP(uri.Port, HandleOptions(uri));
            logger.LogInformation("Added listener endpoint: '{Uri}' via ConfigureKestrel()", uri);
        }
        return options;
        Action<ListenOptions> HandleOptions(Uri uri)
        {
            return listenOptions =>
            {
                try
                {
                    if (uri.Scheme == Uri.UriSchemeHttps) listenOptions.UseHttps();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error adding listener: {Uri}", uri);
                }
            };
        }
    }
}