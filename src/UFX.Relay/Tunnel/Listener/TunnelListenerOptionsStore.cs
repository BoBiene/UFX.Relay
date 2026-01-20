using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel.Listener
{
    public class TunnelListenerOptionsStore : ITunnelListenerOptionsStore
    {
        private TunnelListenerOptions _options;
        private readonly object _lock = new();

        public TunnelListenerOptionsStore(TunnelListenerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public TunnelListenerOptions Current => _options;

        public event EventHandler<(TunnelListenerOptions OldOptions, TunnelListenerOptions NewOptions)>? OptionsChanged;

        public void Update(TunnelListenerOptionsUpdateHandler updateAction)
        {
            TunnelListenerOptions oldOptions;
            TunnelListenerOptions newOptions;
            lock (_lock)
            {
                oldOptions = _options;
                newOptions = updateAction(oldOptions);
                if (newOptions == null)
                {
                    throw new ArgumentNullException(nameof(updateAction), "Update must return a non-null TunnelListenerOptions instance.");
                }

                _options = newOptions;
            }
            OptionsChanged?.Invoke(this, (oldOptions, newOptions));
        }
    }
}
