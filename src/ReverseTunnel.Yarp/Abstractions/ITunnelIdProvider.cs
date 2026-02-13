
namespace ReverseTunnel.Yarp.Abstractions;

public interface ITunnelIdProvider {
    ValueTask<string?> GetTunnelIdAsync();
}