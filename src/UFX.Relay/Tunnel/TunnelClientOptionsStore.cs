
using UFX.Relay.Abstractions;

namespace UFX.Relay.Tunnel
{
    public class TunnelClientOptionsStore(TunnelClientOptions options) : ITunnelClientOptionsStore
    {
        private TunnelClientOptions _options = options;

        public TunnelClientOptions Current
        {
            get { return _options; }
        }

        public event EventHandler<(TunnelClientOptions OldOptions, TunnelClientOptions NewOptions)>? OptionsChanged;

        public void Update(TunnelClientOptionsUpdateHandler updateAction)
        {
            var oldOptions = _options;
            var newOptions = updateAction(_options);
            if (newOptions == null)
            {
                throw new ArgumentNullException(nameof(newOptions), "The update action must return a non-null TunnelClientOptions instance.");
            }
            else
            {
                _options = newOptions;
                OptionsChanged?.Invoke(this, (oldOptions, newOptions));
            }
        }
    }
}
