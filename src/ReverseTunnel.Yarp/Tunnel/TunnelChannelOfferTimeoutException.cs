namespace ReverseTunnel.Yarp.Tunnel;

/// <summary>
/// Thrown when a channel offer was not accepted by the peer within the allotted time.
/// </summary>
/// <remarks>
/// Distinct from <see cref="OperationCanceledException"/>: a cancelled request means the caller
/// went away, this means the tunnel does not serve channels.
/// </remarks>
public sealed class TunnelChannelOfferTimeoutException(string channelName, TimeSpan timeout, Uri? tunnelUri = null)
    : Exception($"The peer did not accept channel '{channelName}' within {timeout.TotalSeconds:F1}s" +
                (tunnelUri is null ? "." : $" on tunnel {tunnelUri}."))
{
    public string ChannelName { get; } = channelName;
    public TimeSpan Timeout { get; } = timeout;
    public Uri? TunnelUri { get; } = tunnelUri;
}
