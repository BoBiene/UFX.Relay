using UFX.Relay.Tunnel;

namespace UFX.Relay.Abstractions
{
    public delegate TunnelClientOptions TunnelClientOptionsUpdateHandler(TunnelClientOptions currentOptions);

    public interface ITunnelClientOptionsStore
    {
        public TunnelClientOptions Current { get; }
        void Update(TunnelClientOptionsUpdateHandler updateAction);
        event EventHandler<(TunnelClientOptions OldOptions, TunnelClientOptions NewOptions)>? OptionsChanged;
    }
}
