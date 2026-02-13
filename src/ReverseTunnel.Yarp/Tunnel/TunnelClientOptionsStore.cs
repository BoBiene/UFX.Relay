
using System.Xml.Linq;
using ReverseTunnel.Yarp.Abstractions;

namespace ReverseTunnel.Yarp.Tunnel
{
    public class TunnelClientOptionsStore(TunnelClientOptions options) : ITunnelClientOptionsStore
    {
        private TunnelClientOptions _options = options;
        private readonly object _lock = new();

        public TunnelClientOptions Current
        {
            get { return _options; }
        }

        public event EventHandler<(TunnelClientOptions OldOptions, TunnelClientOptions NewOptions)>? OptionsChanged;

        public void Update(TunnelClientOptionsUpdateHandler updateAction)
        {
            TunnelClientOptions oldOptions;
            TunnelClientOptions newOptions;

            lock (_lock)
            {
                oldOptions = _options;
                newOptions = updateAction(_options);
                if (newOptions == null)
                {
                    throw new ArgumentNullException(nameof(newOptions), "The update action must return a non-null TunnelClientOptions instance.");
                }
                _options = newOptions;
            }

            OptionsChanged?.Invoke(this, (oldOptions, newOptions));
        }
    }
}
