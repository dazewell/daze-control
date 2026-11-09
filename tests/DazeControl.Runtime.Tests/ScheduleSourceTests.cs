using DazeControl.Core.Model;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

public sealed class ScheduleSourceTests : IDisposable
{
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 9, 22, 0, 0, TimeSpan.Zero)); // a Monday, UTC zone
    private readonly List<(string FlowId, string Text)> _fired = [];
    private readonly List<string> _log = [];
    private bool _paused;

    public void Dispose() => _clock.Dispose();

    private ScheduleSource Source() => new(_clock, (id, text) => _fired.Add((id, text)), () => _paused, _log.Add);

    private static Flow FlowWith(string id, params Trigger[] triggers) =>
        new(id, id, []) { Triggers = [.. triggers] };

    private static AtTrigger At(string time, IfMissed ifMissed = IfMissed.Run, TimeSpan? grace = null) =>
        new(TimeOnly.ParseExact(time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture), Days.Daily) { IfMissed = ifMissed, Grace = grace };

    [Fact]
    public void ArmsOneTimerToTheNextDueTimeAndFiresOnIt()
    {
        using var source = Source();
        source.Apply([FlowWith("a", At("22:30")), FlowWith("b", At("23:00"))]);

        Assert.Equal(1, _clock.TimersCreated);
        Assert.Equal(TimeSpan.FromMinutes(30), _clock.Due);
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal([("a", "Schedule · 22:30")], _fired);
        Assert.Equal(TimeSpan.FromMinutes(30), _clock.Due); // re-armed to 23:00
        Assert.Equal(1, _clock.TimersCreated);
    }

    [Fact]
    public void ResumeWithinGraceRunsOnceAndPastItSkipsWithAReason()
    {
        using var source = Source();
        source.Apply([FlowWith("a", At("22:30")), FlowWith("b", At("22:10"))]);

        _clock.Jump(TimeSpan.FromMinutes(35)); // asleep: a is 5 min late, b was 25 min late
        source.Rearm();

        Assert.Equal([("a", "Schedule · 22:30")], _fired);
        Assert.Contains(_log, l => l.Contains("Skipped: the PC was asleep") && l.Contains('b'));
    }

    [Fact]
    public void IfMissedSkipNeverRunsLate()
    {
        using var source = Source();
        source.Apply([FlowWith("a", At("22:30", IfMissed.Skip))]);

        _clock.Jump(TimeSpan.FromMinutes(35));
        source.Rearm();

        Assert.Empty(_fired);
        Assert.Contains(_log, l => l.Contains("Skipped: the PC was asleep"));
    }

    [Fact]
    public void SeveralMissedIntervalsRunOnce()
    {
        using var source = Source();
        source.Apply([FlowWith("a", new EveryTrigger(TimeSpan.FromMinutes(5)) { Grace = TimeSpan.FromMinutes(10) })]);

        _clock.Jump(TimeSpan.FromMinutes(12)); // 22:05 and 22:10 due; 22:10 is the last
        source.Rearm();

        Assert.Single(_fired);
    }

    [Fact]
    public void PausedSkipsInsteadOfQueueing()
    {
        using var source = Source();
        source.Apply([FlowWith("a", At("22:30"))]);
        _paused = true;

        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Empty(_fired);
        Assert.Contains(_log, l => l.Contains("paused"));
        Assert.Equal(TimeSpan.FromHours(24), _clock.Due); // 22:30 next day
    }

    [Fact]
    public void UnchangedRulesKeepTheirStateAcrossReloads()
    {
        using var source = Source();
        source.Apply([FlowWith("a", At("22:30"))]);
        _clock.Jump(TimeSpan.FromMinutes(35));

        source.Apply([FlowWith("a", At("22:30")), FlowWith("b", At("22:45"))]); // reload during the sleep
        source.Rearm();

        Assert.Equal([("a", "Schedule · 22:30")], _fired); // a kept its cursor; b is new and starts from the reload
    }

    [Fact]
    public void RemovedRulesStopAndAnEmptySetSleeps()
    {
        using var source = Source();
        source.Apply([FlowWith("a", At("22:30"))]);
        source.Apply([FlowWith("a")]);

        Assert.Equal(0, source.Count);
        Assert.Null(source.NextDue);
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty(_fired);
    }

    [Fact]
    public void ClockMovedBackDoesNotFireOrLoseTheNextRun()
    {
        using var source = Source();
        source.Apply([FlowWith("a", At("22:30"))]);
        _clock.Jump(TimeSpan.FromMinutes(-20)); // 21:40
        source.Rearm();

        Assert.Empty(_fired);
        Assert.Equal(TimeSpan.FromMinutes(50), _clock.Due);
    }

    [Fact]
    public void OnceInThePastNeverFires()
    {
        using var source = Source();
        source.Apply([FlowWith("a", new OnceTrigger(new DateTime(2026, 1, 1, 9, 0, 0)))]);

        Assert.Null(source.NextDue);
        Assert.Empty(_fired);
    }

    /// <summary>A clock in the UTC zone with settable time and a single recorded timer.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider, IDisposable
    {
        private readonly FakeTimer _timer = new();
        private DateTimeOffset _now = now;
        private DateTimeOffset? _dueAt;

        public int TimersCreated { get; private set; }

        public void Dispose() => _timer.Dispose();

        public TimeSpan? Due => _dueAt - _now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            _timer.Init(this, callback, state);
            _timer.Change(dueTime, period);
            return _timer;
        }

        /// <summary>Time passes with the timer running: it fires if it came due.</summary>
        public void Advance(TimeSpan by)
        {
            _now += by;
            if (_dueAt is { } due && due <= _now)
            {
                _dueAt = null;
                _timer.Fire();
            }
        }

        /// <summary>The PC slept or the clock changed: time moves, no timer fires.</summary>
        public void Jump(TimeSpan by) => _now += by;

        internal void SetDue(TimeSpan wait) => _dueAt = wait == Timeout.InfiniteTimeSpan ? null : _now + wait;

        private sealed class FakeTimer : ITimer
        {
            private Clock _clock = null!;
            private TimerCallback _callback = null!;
            private object? _state;

            public void Init(Clock clock, TimerCallback callback, object? state) => (_clock, _callback, _state) = (clock, callback, state);

            public void Fire() => _callback(_state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _clock.SetDue(dueTime);
                return true;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
