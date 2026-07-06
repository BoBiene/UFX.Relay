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

        var registered = await RegisterAsync(tunnelId, transportKind, connectionId, cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Tunnel connected: {TunnelId} on instance {InstanceId}", tunnelId, instanceInfo.InstanceId);

        // While the tunnel is alive its registry entry must stay fresh so other replicas can
        // resolve the owning instance; without renewal the entry would expire after RegistryTtl
        // and cross-instance forwarding would break for long-running tunnels.
        using var renewalCts = registered
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        var renewalTask = registered
            ? RenewRegistrationLoopAsync(tunnelId, renewalCts!.Token)
            : Task.CompletedTask;
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
            if (renewalCts is not null)
            {
                await renewalCts.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await renewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            tunnels.TryRemoveTunnel((tunnelId, tunnel));
            await shutdownDisposer.DisposeTunnelAsync().ConfigureAwait(false);
            await tunnelRegistry.UnregisterAsync(tunnelId, instanceInfo.InstanceId, CancellationToken.None).ConfigureAwait(false);
            logger.LogDebug("Tunnel disconnected: {TunnelId} on instance {InstanceId}", tunnelId, instanceInfo.InstanceId);
        }
    }

    private async Task RenewRegistrationLoopAsync(string tunnelId, CancellationToken cancellationToken)
    {
        var interval = GetRenewalInterval(options.Value.RegistryTtl);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await tunnelRegistry.RenewAsync(tunnelId, instanceInfo.InstanceId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Tunnel registry renewal for {TunnelId} stopped: {Message}", tunnelId, ex.Message);
        }
    }

    private static TimeSpan GetRenewalInterval(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(30);
        }

        var half = TimeSpan.FromTicks(ttl.Ticks / 2);
        return half < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : half;
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

    private async ValueTask<bool> RegisterAsync(
        string tunnelId,
        TunnelTransportKind transportKind,
        string? connectionId,
        CancellationToken cancellationToken)
    {
        if (instanceInfo.InternalEndpoint is null)
        {
            return false;
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
        return true;
    }
}
