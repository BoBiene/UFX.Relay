namespace ReverseTunnel.Yarp.Tests.Tunnel;

internal static class TestWait
{
    /// <summary>Polls until <paramref name="condition"/> holds, or throws on timeout.</summary>
    public static async Task ForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"condition not met within {timeout}");
    }
}
