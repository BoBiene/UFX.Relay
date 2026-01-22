namespace UFX.Relay.Tunnel.Listener;

public class TunnelListenerOptions
{
    public string? DefaultTunnelId { get; set; }
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Specifies the delay duration to check for if the tunnel is enabled when the tunnel is disabled, set to 0.5 seconds by default.
    /// </summary>
    public TimeSpan DelayWhenDisabled { get; set; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Specifies the delay duration when a connection is lost, set to 0.5 seconds by default.
    /// </summary>
    public TimeSpan DelayWhenDisconnected { get; set; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Specifies whether to use exponential backoff for reconnection attempts. When enabled, the delay between reconnection attempts increases progressively.
    /// </summary>
    public bool EnableReconnectBackoff { get; set; } = false;

    /// <summary>
    /// Specifies the maximum interval for reconnection attempts when exponential backoff is enabled. Default is 2 minutes.
    /// </summary>
    public TimeSpan MaxReconnectInterval { get; set; } = TimeSpan.FromMinutes(2);
}