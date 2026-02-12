using ReverseTunnel.Yarp.Tunnel;

namespace ReverseTunnel.Yarp.Abstractions
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
        public bool IsEnabled { get; }
        public string LastConnectErrorMessage { get; }
        public string LastErrorResponseBody { get; }
        public int? LastConnectStatusCode { get; }
        public TunnelClient? Tunnel { get; }
        TunnelConnectionState ConnectionState { get; }
        event EventHandler<TunnelConnectionState>? ConnectionStateChanged;
    }
}
