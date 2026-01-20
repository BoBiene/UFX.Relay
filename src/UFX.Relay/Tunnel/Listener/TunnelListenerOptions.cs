namespace UFX.Relay.Tunnel.Listener;

public record TunnelListenerOptions
{
    public string? DefaultTunnelId { get; init; }
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Specifies the delay duration to check for if the tunnel is enabled when the tunnel is disabled, set to 0.5 seconds by default.
    /// </summary>
    public TimeSpan DelayWhenDisabled { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Specifies the delay duration when a connection is lost, set to 0.5 seconds by default.
    /// </summary>
    public TimeSpan DelayWhenDisconnected { get; init; } = TimeSpan.FromSeconds(0.5);
}