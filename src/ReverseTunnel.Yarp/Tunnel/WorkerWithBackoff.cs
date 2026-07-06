using System;
using System.Threading;

namespace ReverseTunnel.Yarp.Tunnel
{
    public class WorkerWithBackoff : IDisposable
    {
        private readonly TimeSpan _initialDelay;
        private readonly TimeSpan _maxDelay;
        private int _errorCount;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Func<CancellationToken, Task<bool>> _func;
        // Tracks the currently running worker loop so that a Reset() can hand its outgoing
        // task to the replacement worker as a predecessor. The replacement awaits it before
        // running the work function, which guarantees two worker generations never execute
        // the work function concurrently (e.g. an in-flight connect racing a fresh one).
        private Task _workerTask = Task.CompletedTask;


        public WorkerWithBackoff(TimeSpan initialDelay, TimeSpan maxDelay, Func<CancellationToken, Task<bool>> func, params CancellationToken[] cancellationTokens)
        {
            _initialDelay = initialDelay;
            _maxDelay = maxDelay;
            _errorCount = 0;
            _cancellationTokenSource = new();
            _func = func;
            CreateWorker(null, [_cancellationTokenSource.Token, ..cancellationTokens]);
        }

        private void CreateWorker(Task? predecessor, params CancellationToken[] tokens)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(tokens);
            var token = cts.Token;
            _workerTask = Task.Run(async () =>
            {
                // Wait for the worker generation this one replaces to fully unwind before
                // running the work function, so a cancelled-but-still-running attempt never
                // overlaps a new one. Its outcome (including cancellation/failure) is not ours.
                if (predecessor is not null)
                {
                    try
                    {
#pragma warning disable VSTHRD003 // Intentional: awaiting the previous worker generation we started ourselves, to serialize the work function.
                        await predecessor.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    }
                    catch
                    {
                        // The predecessor generation's failure is not relevant to this one.
                    }
                }

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var delay = ComputeBackoff();
                        await Task.Delay(delay, token);
                        _errorCount = await _func(token) switch
                        {
                            true => Math.Min(_errorCount + 1, 20),
                            false => 0
                        };
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Expected cancellation, exit gracefully
                }
                catch (Exception)
                {
                    // Unexpected error in the worker function - increment error count and restart the loop.
                    // This is the same worker generation continuing, so no predecessor handoff is needed.
                    _errorCount = Math.Min(_errorCount + 1, 20);
                    var currentCts = _cancellationTokenSource;
                    if (currentCts is not null && !currentCts.IsCancellationRequested)
                    {
                        CreateWorker(null, currentCts.Token);
                    }
                }
                finally
                {
                    cts.Dispose();
                }
            });
        }

        private TimeSpan ComputeBackoff()
        {
            var factor = Math.Pow(2, _errorCount);
            var next = TimeSpan.FromMilliseconds(_initialDelay.TotalMilliseconds * factor);

            if (next > _maxDelay)
                next = _maxDelay;

            // Jitter to prevent many clients from reconnecting at the same time
            var jitterFactor = 1.0 + (Random.Shared.NextDouble() * 0.20);
            var jittered = TimeSpan.FromMilliseconds(next.TotalMilliseconds * jitterFactor);

            return jittered <= _maxDelay ? jittered : _maxDelay;
        }

        public void Reset(params CancellationToken[] cancellationTokens)
        {
            if (_cancellationTokenSource is null)
                throw new ObjectDisposedException(nameof(WorkerWithBackoff));

            _errorCount = 0;
            CancellationTokenSource cancellationTokenSource = new();
            var token = cancellationTokenSource.Token;
            // Capture the outgoing worker so the replacement can await it before it runs the
            // work function. Cancelling the old token source lets that outgoing worker unwind.
            var predecessor = _workerTask;
            var oldCancellationTokenSource = Interlocked.Exchange(ref _cancellationTokenSource, cancellationTokenSource);
            if (oldCancellationTokenSource is not null)
            {
                oldCancellationTokenSource.Cancel();
                oldCancellationTokenSource.Dispose();
            }
            CreateWorker(predecessor, [token, ..cancellationTokens]);
        }

        public void Dispose()
        {
            var oldCancellationTokenSource = Interlocked.Exchange(ref _cancellationTokenSource, null);
            if (oldCancellationTokenSource is not null)
            {
                oldCancellationTokenSource.Cancel();
                oldCancellationTokenSource.Dispose();
            }
        }
    }
}
