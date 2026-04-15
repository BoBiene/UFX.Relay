using ReverseTunnel.Yarp.Tunnel;

namespace ReverseTunnel.Yarp.Tests;

public class WorkerWithBackoffTests
{
    [Fact]
    public async Task Worker_InvokesFunc_OnStart()
    {
        var invoked = new TaskCompletionSource<bool>();
        using var worker = new WorkerWithBackoff(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            _ =>
            {
                invoked.TrySetResult(true);
                return Task.FromResult(false);
            });

        var completed = await Task.WhenAny(invoked.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(invoked.Task, completed);
    }

    [Fact]
    public async Task Worker_StopsOnDispose()
    {
        int callCount = 0;
        var tcs = new TaskCompletionSource();
        var worker = new WorkerWithBackoff(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            async _ =>
            {
                Interlocked.Increment(ref callCount);
                tcs.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10));
                return false;
            });

        // Wait for the first invocation
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        worker.Dispose();

        // After dispose, no further calls should be made (give it a moment)
        var countAfterDispose = callCount;
        await Task.Delay(50);
        Assert.Equal(countAfterDispose, callCount);
    }

    [Fact]
    public async Task Worker_Reset_TriggersReconnect()
    {
        int callCount = 0;
        var secondCallTcs = new TaskCompletionSource();

        using var worker = new WorkerWithBackoff(
            TimeSpan.FromMilliseconds(50), // short initial delay
            TimeSpan.FromSeconds(30),
            _ =>
            {
                int count = Interlocked.Increment(ref callCount);
                if (count >= 2)
                    secondCallTcs.TrySetResult();
                return Task.FromResult(true); // return true to indicate an error (triggers backoff)
            });

        // Wait for first invocation
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(5)), Task.Run(async () =>
        {
            while (callCount < 1)
                await Task.Delay(10);
        }));

        Assert.True(callCount >= 1, "First invocation should have happened");

        // Reset should reset errorCount and trigger another call
        worker.Reset();
        var completed = await Task.WhenAny(secondCallTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(secondCallTcs.Task.IsCompleted, "Second invocation should have happened after Reset()");
    }

    [Fact]
    public void Worker_Dispose_CalledTwice_DoesNotThrow()
    {
        var worker = new WorkerWithBackoff(
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60),
            _ => Task.FromResult(false));

        worker.Dispose();
        // Second dispose should not throw
        worker.Dispose();
    }

    [Fact]
    public void Worker_Reset_AfterDispose_ThrowsObjectDisposedException()
    {
        var worker = new WorkerWithBackoff(
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60),
            _ => Task.FromResult(false));

        worker.Dispose();
        Assert.Throws<ObjectDisposedException>(() => worker.Reset());
    }

    [Fact]
    public async Task Worker_CancellationToken_StopsWorker()
    {
        var cts = new CancellationTokenSource();
        int callCount = 0;
        var firstCallTcs = new TaskCompletionSource();

        using var worker = new WorkerWithBackoff(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            _ =>
            {
                Interlocked.Increment(ref callCount);
                firstCallTcs.TrySetResult();
                return Task.FromResult(false);
            },
            cts.Token);

        // Wait for the first invocation
        await Task.WhenAny(firstCallTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(callCount >= 1);

        // Cancel the token and check that worker stops
        cts.Cancel();
        await Task.Delay(100);
        var countAfterCancel = callCount;
        await Task.Delay(100);
        // No additional calls should have been made after cancellation
        Assert.Equal(countAfterCancel, callCount);
    }
}
