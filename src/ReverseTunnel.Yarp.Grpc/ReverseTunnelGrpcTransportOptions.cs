using Grpc.Net.Client;

namespace ReverseTunnel.Yarp.Grpc;

public sealed class ReverseTunnelGrpcTransportOptions
{
    public Action<GrpcChannelOptions>? ConfigureChannel { get; set; }
    public string ConnectionIdPrefix { get; set; } = "grpc";
    public TimeSpan ClientKeepAlivePingDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ClientKeepAlivePingTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ServerKeepAlivePingDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ServerKeepAlivePingTimeout { get; set; } = TimeSpan.FromSeconds(15);
}