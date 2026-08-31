namespace ReverseTunnel.Yarp.Tunnel;

/// <summary>
/// Snapshot of a tunnel's state, logged when a connection is established, replaced or lost.
/// </summary>
/// <param name="ConnectionId">Identifies the connection in both peers' logs.</param>
/// <param name="Transport">The transport's own description of its state.</param>
/// <param name="MuxCompletion">Whether the multiplexing stream has completed, and how.</param>
/// <param name="Age">How long the tunnel has been up.</param>
/// <param name="IdleTime">Time since the last channel served or accepted.</param>
/// <param name="ChannelsServed">Channels served since the tunnel was established.</param>
public readonly record struct TunnelDiagnostics(
    string ConnectionId,
    string Transport,
    string MuxCompletion,
    TimeSpan Age,
    TimeSpan IdleTime,
    long ChannelsServed)
{
    public override string ToString() =>
        $"connectionId={ConnectionId}, transport=[{Transport}], mux={MuxCompletion}, " +
        $"age={Age.TotalSeconds:F0}s, idle={IdleTime.TotalSeconds:F0}s, channels={ChannelsServed}";
}
