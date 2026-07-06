using Microsoft.Extensions.Options;

namespace ReverseTunnel.Yarp.Tunnel;

public sealed class ReverseTunnelInstanceInfo
{
    public ReverseTunnelInstanceInfo(IOptions<ReverseTunnelOptions> options)
    {
        var configured = options.Value.InstanceId;
        InstanceId = string.IsNullOrWhiteSpace(configured)
            ? $"{Environment.MachineName}-{Guid.NewGuid():N}"
            : configured;
        InternalEndpoint = options.Value.InternalEndpoint;
    }

    public string InstanceId { get; }
    public Uri? InternalEndpoint { get; }
}
