using UFX.Relay.Tunnel.Listener;

namespace UFX.Relay.Abstractions
{
    public delegate TunnelListenerOptions TunnelListenerOptionsUpdateHandler(TunnelListenerOptions currentOptions);

    public interface ITunnelListenerOptionsStore
    {
        TunnelListenerOptions Current { get; }
        void Update(TunnelListenerOptionsUpdateHandler updateAction);
        event EventHandler<(TunnelListenerOptions OldOptions, TunnelListenerOptions NewOptions)>? OptionsChanged;
    }
}
