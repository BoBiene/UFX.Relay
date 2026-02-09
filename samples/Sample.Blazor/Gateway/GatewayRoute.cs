namespace Sample.Blazor.Gateway;

public sealed record GatewayRoute
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string PathPrefix { get; init; }
    public required string DestinationBaseUrl { get; init; }
    public bool StripPrefix { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
}
