using UFX.Relay.Tunnel;

namespace UFX.Relay.Abstractions
{
    public enum TunnelConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Error
    }

    public interface ITunnelClientManager
    {
        public TunnelClient? Tunnel { get; }
        TunnelConnectionState ConnectionState { get; }
        event EventHandler<TunnelConnectionState>? ConnectionStateChanged;
    }
}
