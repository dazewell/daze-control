using DazeControl.Core.Model;

namespace DazeControl.Runtime.Triggers;

/// <summary>
/// Runs the flows' schedule triggers (M20, D-107): ONE due-time timer armed to the next due time over all rules, nothing
/// between due times. <see cref="Apply"/> diffs the rules (an unchanged rule keeps its cursor); <see cref="Rearm"/> after a
/// power resume or a clock change fires what came due meanwhile, once per rule, inside its grace, and never replays several
/// missed intervals. A rule whose arithmetic throws disables only itself (D-021).
/// </summary>
/// <param name="time">Clock, local zone and the timer.</param>
/// <param name="fire">Starts the flow (flow id, history text); the caller observes the task.</param>
/// <param name="paused">Pause silences schedules too (they are skipped, not queued).</param>
/// <param name="log">One line per skipped or disabled schedule.</param>
public sealed class ScheduleSource(TimeProvider time, Action<string, string> fire, Func<bool> paused, Action<string> log) : IDisposable
{
    /// <summary>The grace of a rule that sets none.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    /// <summary>How late a due time may fire before it counts as missed (timer jitter).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    private const int MaxSteps = 100_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<(string FlowId, int Index), Rule> _rules = [];
    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Rules now watched.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _rules.Count;
        }
    }

    /// <summary>The next due time over all rules; null when there is none.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_lock)
                return _rules.Values.Where(r => !r.Dead).Select(r => r.Next).Min();
        }
    }

    /// <summary>New rule set (config load): new rules start from now, removed ones go, unchanged ones keep their cursor.</summary>
    public void Apply(IEnumerable<Flow> flows)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_disposed)
                return;
            var next = new Dictionary<(string, int), Rule>();
            foreach (var flow in flows)
            {
                // Keyed by the trigger's index: identical entries stay separate rules (the validator keeps them runnable).
                for (var i = 0; i < flow.Triggers.Count; i++)
                {
                    if (flow.Triggers[i] is not ScheduleTrigger rule || !TriggerText.IsValidSchedule(rule)) // the validator calls the others ignored; `only` on a schedule is one of them
                        continue;
                    var key = (flow.Id, i);
                    next[key] = _rules.TryGetValue(key, out var kept) && kept.Trigger == rule ? kept : Compute(new Rule(flow.Id, flow.Name, rule, now), now);
                }
            }
            _rules.Clear();
            foreach (var pair in next)
                _rules[pair.Key] = pair.Value;
            ArmLocked(now);
        }
    }

    /// <summary>Power resume or clock change: runs what came due meanwhile (inside its grace), then re-arms.</summary>
    public void Rearm() => Tick();

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _rules.Clear();
        }
    }

    private void Tick()
    {
        List<(string FlowId, string Text)> due = [];
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_disposed)
                return;
            foreach (var rule in _rules.Values.ToList())
            {
                if (rule.Dead)
                    continue;
                if (now < rule.Cursor)
                {
                    // The clock went back: what was handled stays handled. The cursor and the next due time (always after
                    // it) are kept, so an occurrence that already ran doesn't run again when the clock passes it.
                    continue;
                }
                if (rule.Next is not { } next || next > now)
                    continue;
                var last = next;
                try
                {
                    var steps = 0;
                    while (ScheduleTable.Next(rule.Trigger, last, time) is { } more && more <= now && ++steps < MaxSteps)
                        last = more;
                }
#pragma warning disable CA1031 // fail-soft: a bad schedule disables only itself
                catch (Exception e)
#pragma warning restore CA1031
                {
                    Disable(rule, e);
                    continue;
                }
                var text = TriggerText.Describe(rule.Trigger);
                var late = now - last;
                var grace = rule.Trigger.Grace ?? DefaultGrace;
                if (paused())
                    log($"schedule: {rule.FlowName} ({text}) skipped: schedules are paused");
                else if (late > (rule.Trigger.IfMissed == IfMissed.Skip ? Tolerance : grace + Tolerance))
                    log($"schedule: {rule.FlowName} ({text}) Skipped: the PC was asleep");
                else
                    due.Add((rule.FlowId, text));
                rule.Cursor = now;
                Compute(rule, now);
            }
            ArmLocked(now);
        }
        foreach (var (flowId, text) in due)
        {
            try
            {
                fire(flowId, text);
            }
#pragma warning disable CA1031 // fail-soft: one flow failing to start must not block the others
            catch (Exception e)
#pragma warning restore CA1031
            {
                log($"schedule: {flowId} failed to start: {e.Message}");
            }
        }
    }

    private Rule Compute(Rule rule, DateTimeOffset after)
    {
        try
        {
            rule.Next = ScheduleTable.Next(rule.Trigger, after, time);
        }
#pragma warning disable CA1031 // fail-soft: a bad schedule disables only itself
        catch (Exception e)
#pragma warning restore CA1031
        {
            Disable(rule, e);
        }
        return rule;
    }

    private void Disable(Rule rule, Exception e)
    {
        rule.Dead = true;
        rule.Next = null;
        log($"schedule: {rule.FlowName} ({TriggerText.Describe(rule.Trigger)}) disabled: {e.Message}");
    }

    /// <summary>Under the lock. Fires <see cref="Tick"/> at the earliest due time; with none left the timer sleeps.</summary>
    private void ArmLocked(DateTimeOffset now)
    {
        var next = _rules.Values.Where(r => !r.Dead).Select(r => r.Next).Min();
        _timer ??= time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (next is null)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        // A timer's limit is ~49 days: a later due time wakes early, finds nothing due and re-arms.
        var wait = TimeSpan.FromTicks(Math.Clamp((next.Value - now).Ticks, 0, TimeSpan.FromDays(30).Ticks));
        _timer.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private sealed class Rule(string flowId, string flowName, ScheduleTrigger trigger, DateTimeOffset cursor)
    {
        public string FlowId { get; } = flowId;

        public string FlowName { get; } = flowName;

        public ScheduleTrigger Trigger { get; } = trigger;

        /// <summary>Everything at or before this has been handled.</summary>
        public DateTimeOffset Cursor { get; set; } = cursor;

        public DateTimeOffset? Next { get; set; }

        public bool Dead { get; set; }
    }
}
