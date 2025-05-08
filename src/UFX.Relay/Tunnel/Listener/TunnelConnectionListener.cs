using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel.Listener;

public sealed class TunnelConnectionListener : IConnectionListener
{
    private readonly TunnelEndpoint _endpoint;
    private readonly ITunnelIdProvider _tunnelIdProvider;
    private readonly ITunnelClientManager _tunnelManager;
    private readonly IOptions<TunnelListenerOptions> _options;
    private readonly ILogger<TunnelConnectionListener> _logger;
    private CancellationTokenSource _unbindTokenSource = new();
    public EndPoint EndPoint => _endpoint;

    public TunnelConnectionListener(
        TunnelEndpoint endpoint,
        ITunnelIdProvider tunnelIdProvider,
        ITunnelClientManager tunnelManager,
        IOptions<TunnelListenerOptions> options,
        ILogger<TunnelConnectionListener> logger)
    {
        _endpoint = endpoint;
        _tunnelIdProvider = tunnelIdProvider;
        _tunnelManager = tunnelManager;
        _options = options;
        _logger = logger;

        endpoint.TunnelClientManager = tunnelManager;
    }



    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_unbindTokenSource.Token, cancellationToken);
        var linkedToken = linkedCts.Token;

        while (!linkedToken.IsCancellationRequested)
        {
            if (!_tunnelManager.IsEnabled)
            {
                //tunnel is disabled, wait for it to be enabled
                await Task.Delay(_options.Value.DelayWhenDisabled, linkedToken);
            }
            else if (_tunnelManager.ConnectionState != TunnelConnectionState.Connected)
            {
                //tunnel is disconnected, wait for it to be connected
                await Task.Delay(_options.Value.DelayWhenDisconnected, linkedToken);
            }
            else
            {
                var tunnel = _endpoint.Tunnel;
                if (tunnel == null || tunnel.Completion.Status == TaskStatus.RanToCompletion)
                {
                    _logger.LogWarning("No tunnel available ({Tunnel}, {TunnelStatus})", tunnel, tunnel?.Completion.Status);
                    await Task.Delay(_options.Value.DelayWhenDisconnected, linkedToken);
                }
                else
                {
                    try
                    {
                        var channel = await tunnel.GetChannelAsync(tunnel is TunnelHost ? Guid.NewGuid().ToString("N") : null, linkedToken);
                        return new TunnelConnectionContext(channel.QualifiedId.ToString(), channel, _endpoint);
                    }
                    catch (UnderlyingStreamClosedException uscx)
                    {
                        _logger.LogDebug(uscx, "Underlying stream closed while waiting for channel.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error in AcceptAsync.");
                    }
                }
            }
        }

        return null;
    }

    public async Task BindAsync()
    {
        var oldToken = _unbindTokenSource;
        _unbindTokenSource = new CancellationTokenSource();
        oldToken.Dispose(); // 🔧 Dispose alte Quelle

        _endpoint.TunnelId = await _tunnelIdProvider.GetTunnelIdAsync()
            ?? throw new KeyNotFoundException("TunnelId not found, you need to configure a tunnel-id");
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        await _unbindTokenSource.CancelAsync();
        if (_endpoint.Tunnel is not null)
        {
            await _endpoint.Tunnel.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindAsync();
        _unbindTokenSource.Dispose();
    }
}
