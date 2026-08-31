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

            // Bounded on purpose: this runs inside a request, and an unbounded retry loop kept the
            // caller waiting indefinitely whenever the upstream was unreachable.
            const int C_MAX_CONNECT_ATTEMPTS = 3;
            TunnelTransportConnection? connection = null;
            for (int attempt = 1; connection is null && attempt <= C_MAX_CONNECT_ATTEMPTS; attempt++)
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
                    if (attempt == C_MAX_CONNECT_ATTEMPTS)
                    {
                        logger.LogInformation(
                            "Giving up connecting tunnel {TunnelId} after {Attempts} attempts: {Message}",
                            tunnelId, C_MAX_CONNECT_ATTEMPTS, ex.Message);
                        return null;
                    }
                    await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                }
            }

            if (connection is null) return null;

            logger.LogInformation("Connected to {Uri}", connection.Uri);
            var stream = await MultiplexingStream.CreateAsync(connection.Stream, new MultiplexingStream.Options
            {
                ProtocolMajorVersion = 3
            }, cancellationToken).ConfigureAwait(false);
            var tunnel = new TunnelClient(connection, stream, logger) { Uri = connection.Uri };
            tunnel.Completion.ContinueWith(_ =>
            {
                logger.LogDebug("Removing tunnel {TunnelId}, uri: {Uri}", tunnelId, connection.Uri);
                return tunnels.TryRemoveTunnel((tunnelId, tunnel));
            }, TaskScheduler.Default);
            return tunnels.GetOrAdd(tunnelId, tunnel);
        }
    }
}