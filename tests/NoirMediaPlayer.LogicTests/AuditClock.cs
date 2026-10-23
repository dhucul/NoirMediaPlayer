namespace NoirMediaPlayer.LogicTests;

// Only one-shot timers are needed by CancellationTokenSource in these tests.
internal sealed class AuditClock : TimeProvider
{
    private long _ticks;
    private readonly List<ClockTimer> _timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }
    internal void Advance(TimeSpan elapsed)
    {
        Interlocked.Add(ref _ticks, elapsed.Ticks);
        ClockTimer[] timers;
        lock (_timers) timers = _timers.ToArray();
        foreach (var timer in timers) timer.Fire();
    }
    private sealed class ClockTimer(AuditClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _due = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("One-shot timers only");
            Interlocked.Exchange(ref _due, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks);
            return true;
        }
        internal void Fire()
        {
            var due = Interlocked.Read(ref _due);
            if (due <= clock.GetTimestamp() && Interlocked.CompareExchange(ref _due, long.MaxValue, due) == due)
                callback(state);
        }
        public void Dispose() => Interlocked.Exchange(ref _due, long.MaxValue);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
