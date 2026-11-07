namespace DazeControl.Providers.Tests;

/// <summary>
/// Hand-moved clock (ported from the OSD's TestDoubles, D-017). Thread-safe, because the runner creates timers
/// from pool threads; <see cref="WaitForTimerAsync"/> lets a test wait until the code under test armed one
/// before it advances, so no advance is ever lost to a race.
/// </summary>
internal sealed class ManualTime : TimeProvider, IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly SemaphoreSlim _created = new(0);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
            return _ticks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            timer.Change(dueTime, period);
            _timers.Add(timer);
        }
        _created.Release();
        return timer;
    }

    /// <summary>Completes once a timer has been created since the last call (counting each creation once).</summary>
    public async Task WaitForTimerAsync()
    {
        if (!await _created.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            throw new TimeoutException("No timer was created.");
    }

    public void Dispose() => _created.Dispose();

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _ticks += by.Ticks;
            due = _timers.Where(t => t.TakeIfDue(_ticks)).ToList();
        }
        foreach (var timer in due)
            timer.Run(); // outside the lock: callbacks may create timers
    }

    internal sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        private long? _dueAt;

        public void Run() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time._lock)
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : time._ticks + dueTime.Ticks;
            return true;
        }

        /// <summary>One-shot: periodic timers aren't used by the runner. Called under the clock's lock.</summary>
        public bool TakeIfDue(long now)
        {
            if (_dueAt is not { } due || due > now)
                return false;
            _dueAt = null;
            return true;
        }

        public void Dispose()
        {
            lock (time._lock)
                _dueAt = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
