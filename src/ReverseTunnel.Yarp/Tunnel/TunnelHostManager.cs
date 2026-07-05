using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel.Registry;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel;

public class TunnelHostManager : ITunnelHostManager
{
    private readonly ILogger<TunnelHostManager> logger;
    private readonly ITunnelCollectionProvider tunnelCollectionProvider;
    private readonly ITunnelServerTransport serverTransport;
    private readonly ITunnelRegistry tunnelRegistry;
    private readonly ReverseTunnelInstanceInfo instanceInfo;
    private readonly IOptions<ReverseTunnelOptions> options;
    private readonly IHostApplicationLifetime? appLifetime;

    public TunnelHostManager(
        ILogger<TunnelHostManager> logger,
        ITunnelCollectionProvider tunnelCollectionProvider)
        : this(
            logger,
            tunnelCollectionProvider,
            new WebSocketTunnelServerTransport(),
            new InMemoryTunnelRegistry(),
            new ReverseTunnelInstanceInfo(Options.Create(new ReverseTunnelOptions())),
            Options.Create(new ReverseTunnelOptions()),
            null)
    {
    }

    public TunnelHostManager(
        ILogger<TunnelHostManager> logger,
        ITunnelCollectionProvider tunnelCollectionProvider,
        ITunnelServerTransport serverTransport,
        ITunnelRegistry tunnelRegistry,
        ReverseTunnelInstanceInfo instanceInfo,
        IOptions<ReverseTunnelOptions> options,
        IHostApplicationLifetime? appLifetime = null)
    {
        this.logger = logger;
        this.tunnelCollectionProvider = tunnelCollectionProvider;
        this.serverTransport = serverTransport;
        this.tunnelRegistry = tunnelRegistry;
        this.instanceInfo = instanceInfo;
        this.options = options;
        this.appLifetime = appLifetime;
    }

    public virtual async Task<Tunnel?> GetOrCreateTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default)
    {
        var tunnels = await tunnelCollectionProvider.GetTunnelCollectionAsync(context, cancellationToken).ConfigureAwait(false);
        if (tunnels.TryGetTunnel(tunnelId, out var existingTunnel))
            return existingTunnel;

        return null;
    }

    public async Task StartTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default)
    {
        if (!serverTransport.CanAccept(context))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var connection = await serverTransport.AcceptAsync(context, tunnelId, cancellationToken).ConfigureAwait(false);
        await StartTunnelAsync(connection, tunnelId, context, serverTransport.Kind, context.Connection.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartTunnelAsync(
        TunnelTransportConnection connection,
        string tunnelId,
        HttpContext? context,
        TunnelTransportKind transportKind,
        string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var collectionContext = context ?? new DefaultHttpContext();
        var tunnels = await tunnelCollectionProvider.GetTunnelCollectionAsync(collectionContext, cancellationToken).ConfigureAwait(false);

        var stream = await MultiplexingStream.CreateAsync(connection.Stream, new MultiplexingStream.Options
        {
            ProtocolMajorVersion = 3
        }, cancellationToken).ConfigureAwait(false);

        var tunnel = new TunnelHost(connection, stream) { Uri = connection.Uri };
        tunnels.AddOrUpdate(tunnelId, _ => tunnel, (_, oldTunnel) =>
        {
            oldTunnel.Dispose();
            return tunnel;
        });
        var shutdownDisposer = new ShutdownTunnelDisposer(tunnel);
        using var stoppingRegistration = appLifetime?.ApplicationStopping.Register(
            static state => ((ShutdownTunnelDisposer)state!).Start(),
            shutdownDisposer) ?? default;

        await RegisterAsync(tunnelId, transportKind, connectionId, cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Tunnel connected: {TunnelId} on instance {InstanceId}", tunnelId, instanceInfo.InstanceId);
        try
        {
            await stream.Completion.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Tunnel: {TunnelId}, Message: {Message}", tunnelId, e.Message);
        }
        finally
        {
            tunnels.TryRemoveTunnel((tunnelId, tunnel));
            await shutdownDisposer.DisposeTunnelAsync().ConfigureAwait(false);
            await tunnelRegistry.UnregisterAsync(tunnelId, instanceInfo.InstanceId, CancellationToken.None).ConfigureAwait(false);
            logger.LogDebug("Tunnel disconnected: {TunnelId} on instance {InstanceId}", tunnelId, instanceInfo.InstanceId);
        }
    }

    private sealed class ShutdownTunnelDisposer(TunnelHost tunnel)
    {
        private readonly object sync = new();
        private Task? disposeTask;

        public void Start()
        {
            var task = GetOrCreateDisposeTaskAsync();
            _ = task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        public Task DisposeTunnelAsync() => GetOrCreateDisposeTaskAsync();

        private Task GetOrCreateDisposeTaskAsync()
        {
            lock (sync)
            {
                return disposeTask ??= tunnel.DisposeAsync().AsTask();
            }
        }
    }

    private async ValueTask RegisterAsync(
        string tunnelId,
        TunnelTransportKind transportKind,
        string? connectionId,
        CancellationToken cancellationToken)
    {
        if (instanceInfo.InternalEndpoint is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await tunnelRegistry.RegisterAsync(new TunnelRegistration(
            tunnelId,
            instanceInfo.InstanceId,
            instanceInfo.InternalEndpoint,
            transportKind,
            now,
            now.Add(options.Value.RegistryTtl),
            connectionId), cancellationToken).ConfigureAwait(false);
    }
}
