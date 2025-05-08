using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel.Listener;

public sealed class TunnelConnectionListener(
    TunnelEndpoint endpoint,
    ITunnelIdProvider tunnelIdProvider,
    ITunnelClientManager tunnelManager,
    IOptions<TunnelListenerOptions> options,
    ILogger<TunnelConnectionListener> logger) : IConnectionListener
{
    private readonly SemaphoreSlim getTunnelSemaphore = new(1, 1);
    private CancellationTokenSource unbindTokenSource = new();
    public EndPoint EndPoint => endpoint;

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(unbindTokenSource.Token, cancellationToken);
        var linkedToken = linkedCts.Token;

        while (!linkedToken.IsCancellationRequested)
        {
            if (!tunnelManager.IsEnabled)
            {
                //tunnel is disabled, wait for it to be enabled
                await Task.Delay(500, linkedToken);
            }
            else
            {
                await GetTunnelAsync(linkedToken);

                if (endpoint.Tunnel == null)
                {
                    logger.LogWarning("No tunnel available after GetTunnelAsync.");
                    return null;
                }

                try
                {
                    var channel = await endpoint.Tunnel
                        .GetChannelAsync(endpoint.Tunnel is TunnelHost ? Guid.NewGuid().ToString("N") : null, linkedToken);
                    return new TunnelConnectionContext(channel.QualifiedId.ToString(), channel, endpoint);
                }
                catch (UnderlyingStreamClosedException uscx)
                {
                    logger.LogDebug(uscx, "Underlying stream closed while waiting for channel.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error in AcceptAsync.");
                    return null;
                }
            }
        }

        return null;
    }

    public async Task BindAsync()
    {
        var oldToken = unbindTokenSource;
        unbindTokenSource = new CancellationTokenSource();
        oldToken.Dispose(); // 🔧 Dispose alte Quelle

        endpoint.TunnelId = await tunnelIdProvider.GetTunnelIdAsync()
            ?? throw new KeyNotFoundException("TunnelId not found, you need to configure a tunnel-id");

        _ = Task.Run(() => ReconnectTunnelAsync(unbindTokenSource.Token)); // fire-and-forget
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        await unbindTokenSource.CancelAsync();
        if (endpoint.Tunnel is not null)
        {
            await endpoint.Tunnel.DisposeAsync();
            endpoint.Tunnel = null;
        }
    }

    private async ValueTask GetTunnelAsync(CancellationToken cancellationToken = default)
    {
        if (endpoint.Tunnel is { Completion.IsCompleted: false }) return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(unbindTokenSource.Token, cancellationToken);
        var linkedToken = linkedCts.Token;

        await getTunnelSemaphore.WaitAsync(linkedToken);
        try
        {
            while (endpoint.Tunnel is not { Completion.IsCompleted: false })
            {
                var newTunnel = tunnelManager.Tunnel;
                endpoint.Tunnel = newTunnel;
                await Task.Delay(50, linkedToken);
            }
        }
        finally
        {
            getTunnelSemaphore.Release();
        }
    }

    private async Task ReconnectTunnelAsync(CancellationToken cancellationToken = default)
    {
        var timer = new PeriodicTimer(options.Value.ReconnectInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await GetTunnelAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException e)
        {
            logger.LogInformation(e, "ReconnectTunnelAsync cancelled.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindAsync();
        unbindTokenSource.Dispose();
    }
}
