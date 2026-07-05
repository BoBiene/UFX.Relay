using Nerdbank.Streams;
using ReverseTunnel.Yarp.Abstractions;
using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel
{
    public class TunnelHostAggregatedManager : TunnelHostManager
    {
        private readonly ILogger<TunnelHostAggregatedManager> logger;
        private readonly ITunnelClientOptionsStore optionsStore;
        private readonly ITunnelClientTransport clientTransport;
        private readonly ITunnelCollectionProvider tunnelCollectionProvider;

        public TunnelHostAggregatedManager(
            ILogger<TunnelHostManager> baseLogger,
            ILogger<TunnelHostAggregatedManager> logger,
            ITunnelCollectionProvider tunnelCollectionProvider,
            ITunnelClientOptionsStore optionsStore,
            ITunnelClientTransport clientTransport)
            : base(baseLogger, tunnelCollectionProvider)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.tunnelCollectionProvider = tunnelCollectionProvider ?? throw new ArgumentNullException(nameof(tunnelCollectionProvider));
            this.optionsStore = optionsStore ?? throw new ArgumentNullException(nameof(optionsStore));
            this.clientTransport = clientTransport ?? throw new ArgumentNullException(nameof(clientTransport));
        }

        public override async Task<Tunnel?> GetOrCreateTunnelAsync(HttpContext context, string tunnelId, CancellationToken cancellationToken = default)
        {
            var tunnels = await tunnelCollectionProvider.GetTunnelCollectionAsync(context, cancellationToken).ConfigureAwait(false);
            if (tunnels.TryGetTunnel(tunnelId, out var existingTunnel)) return existingTunnel;

            TunnelTransportConnection? connection = null;
            while (connection is null)
            {
                try
                {
                    connection = await clientTransport.ConnectAsync(
                        new TunnelClientTransportContext(optionsStore.Current, tunnelId),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TunnelTransportException ex)
                {
                    logger.LogDebug(ex, "Tunnel transport error: {Uri}, {Message}", ex.Uri, ex.Message);
                    await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                }
            }

            logger.LogInformation("Connected to {Uri}", connection.Uri);
            var stream = await MultiplexingStream.CreateAsync(connection.Stream, new MultiplexingStream.Options
            {
                ProtocolMajorVersion = 3
            }, cancellationToken).ConfigureAwait(false);
            var tunnel = new TunnelClient(connection, stream) { Uri = connection.Uri };
            tunnel.Completion.ContinueWith(_ =>
            {
                logger.LogDebug("Removing tunnel {TunnelId}, uri: {Uri}", tunnelId, connection.Uri);
                return tunnels.TryRemoveTunnel((tunnelId, tunnel));
            }, TaskScheduler.Default);
            return tunnels.GetOrAdd(tunnelId, tunnel);
        }
    }
}