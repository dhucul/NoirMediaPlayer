namespace NoirMediaPlayer.Services;

/// <summary>Runs native playback operations in order, away from the dispatcher.</summary>
internal sealed class PlaybackOperationQueue
{
    /// <summary>A native operation running longer than this is reported as unresponsive.</summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(20);

    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private long _operationStarted;

    public bool IsIdle
    {
        get
        {
            lock (_sync)
            {
                return _tail.IsCompleted;
            }
        }
    }

    /// <summary>How long the current operation has run, or null between operations.</summary>
    public TimeSpan? RunningFor
    {
        get
        {
            var started = Interlocked.Read(ref _operationStarted);
            return started == 0 ? null : System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
    }

    public Task Enqueue(Action operation)
    {
        lock (_sync)
        {
            var previous = _tail;
            return _tail = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                }
                catch
                {
                    // A failed operation must not prevent cleanup or the next request.
                }
                // A stalled operation is only reported. Overtaking it could start
                // a new native session before the previous one was torn down.
                Interlocked.Exchange(ref _operationStarted, Math.Max(1, System.Diagnostics.Stopwatch.GetTimestamp()));
                try
                {
                    operation();
                }
                finally
                {
                    Interlocked.Exchange(ref _operationStarted, 0);
                }
            });
        }
    }
}
