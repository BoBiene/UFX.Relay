namespace Sample.Chat.Client.Services;

public sealed class ChatClientResilienceOptions
{
    public TimeSpan UnaryCallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan StreamWriteTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan StreamInitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan StreamMaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    public double StreamBackoffMultiplier { get; set; } = 2;

    public TimeSpan StreamReconnectJitter { get; set; } = TimeSpan.FromMilliseconds(250);
}
