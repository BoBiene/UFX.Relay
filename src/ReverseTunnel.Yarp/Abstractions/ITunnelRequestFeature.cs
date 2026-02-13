namespace ReverseTunnel.Yarp.Abstractions;

/// <summary>
/// Feature that indicates a request came through a ReverseTunnel.Yarp tunnel connection.
/// This feature is set on all requests received via the tunnel listener.
/// </summary>
public interface ITunnelRequestFeature
{
    /// <summary>
    /// Gets the tunnel ID for this connection.
    /// </summary>
    string TunnelId { get; }
}
