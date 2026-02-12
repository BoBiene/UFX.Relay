namespace ReverseTunnel.Yarp.Abstractions
{
    public interface ITunnelCollectionProvider
    {
        Task<ITunnelCollection> GetTunnelCollectionAsync(HttpContext context, CancellationToken cancellationToken = default);
    }
}
