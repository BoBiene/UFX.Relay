using ReverseTunnel.Yarp.Tunnel.Transport;

namespace ReverseTunnel.Yarp.Tunnel.Registry;

public sealed record TunnelRegistration(
    string TunnelId,
    string InstanceId,
    Uri InternalEndpoint,
    TunnelTransportKind Transport,
    DateTimeOffset LastSeen,
    DateTimeOffset ExpiresAt,
    string? ConnectionId = null);
