using System;
using System.Threading;

namespace UFX.Relay.Tunnel
{
    public class WorkerWithBackoff : IDisposable
    {
        private readonly TimeSpan _initialDelay;
        private readonly TimeSpan _maxDelay;
        private int _errorCount;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Func<CancellationToken, Task<bool>> _func;


        public WorkerWithBackoff(TimeSpan initialDelay, TimeSpan maxDelay, Func<CancellationToken, Task<bool>> func, params CancellationToken[] cancellationTokens)
        {
            _initialDelay = initialDelay;
            _maxDelay = maxDelay;
            _errorCount = 0;
            _cancellationTokenSource = new();
            _func = func;
            CreateWorker([_cancellationTokenSource.Token, ..cancellationTokens]);
        }

        private void CreateWorker(params CancellationToken[] tokens)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(tokens);
            var token = cts.Token;
            _ = Task.Factory.StartNew(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var delay = ComputeBackoff();
                    try
                    {
                        await Task.Delay(delay, token);
                        _errorCount = await _func(token) switch
                        {
                            true => Math.Min(_errorCount + 1, 20),
                            false => 0
                        };

                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
                cts.Dispose();
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
            var oldCancellationTokenSource = Interlocked.Exchange(ref _cancellationTokenSource, cancellationTokenSource);
            if (oldCancellationTokenSource is not null)
            {
                oldCancellationTokenSource.Cancel();
                oldCancellationTokenSource.Dispose();
            }
            CreateWorker([token, ..cancellationTokens]);
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
