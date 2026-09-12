namespace NoirMediaPlayer.Services;

/// <summary>Runs native playback operations in order, away from the dispatcher.</summary>
internal sealed class PlaybackOperationQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

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
                operation();
            });
        }
    }
}
