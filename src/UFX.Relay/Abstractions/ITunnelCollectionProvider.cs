namespace UFX.Relay.Abstractions
{
    public interface ITunnelCollectionProvider
    {
        Task<ITunnelCollection> GetTunnelCollectionAsync(HttpContext context, CancellationToken cancellationToken = default);
    }
}
