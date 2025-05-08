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
}