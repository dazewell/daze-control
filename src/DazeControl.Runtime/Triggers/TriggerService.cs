using DazeControl.Core.Model;
using DazeControl.Runtime.Control;

namespace DazeControl.Runtime.Triggers;

/// <summary>Guards every event trigger shares (DESIGN §3).</summary>
/// <param name="Debounce">Events of one name closer together than this count once: a burst (a dock plugging in four
/// devices) starts a flow once. Measured from the latest event, so a long burst stays one.</param>
/// <param name="Cooldown">After a trigger started its flow, it can't start it again for this long.</param>
/// <param name="ConditionTimeout">How long <c>only</c> may take; past it the trigger fires anyway.</param>
public sealed record TriggerOptions(TimeSpan Debounce, TimeSpan Cooldown, TimeSpan ConditionTimeout)
{
    public static TriggerOptions Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
}

/// <summary>
/// Starts flows from system events (M20, D-107). <see cref="Apply"/> takes the runnable flows on every config change,
/// and starts or stops each source to match who subscribes (nothing subscribed = no source loaded). An event then goes
/// through: paused → dropped; debounce; per trigger cooldown; <c>only</c>; the chain guard; and finally
/// <see cref="FlowLauncher.TriggerAsync"/>, so <c>whenRunning</c>, one run slot per flow and history work as for a
/// hotkey. No timers: debounce and cooldown compare timestamps. Thread-safe; events may come from any thread.
/// </summary>
public sealed class TriggerService : IDisposable
{
    private readonly ITriggerSource[] _sources;
    private readonly FlowLauncher _launcher;
    private readonly IFlowStarter _flows;
    private readonly TimeProvider _time;
    private readonly Func<bool> _paused;
    private readonly Dictionary<string, ITriggerCondition> _conditions;
    private readonly Action<string> _log;
    private readonly TriggerOptions _options;
    private readonly CancellationTokenSource _closed = new();
    private readonly Lock _lock = new();
    private readonly HashSet<Task> _inFlight = [];
    private readonly Dictionary<(string Name, string? Detail), long> _lastEvent = [];
    private readonly Dictionary<Subscription, long> _lastStart = [];
    private readonly HashSet<ITriggerSource> _started = [];

    private Dictionary<string, List<Subscription>> _byEvent = [];
    private bool _disposed;

    /// <param name="paused">True while the tray's Pause is on: events are dropped, not queued.</param>
    public TriggerService(IEnumerable<ITriggerSource> sources, FlowLauncher launcher, IFlowStarter flows, TimeProvider time,
        Func<bool> paused, IEnumerable<ITriggerCondition>? conditions = null, Action<string>? log = null, TriggerOptions? options = null)
    {
        _sources = [.. sources];
        _launcher = launcher;
        _flows = flows;
        _time = time;
        _paused = paused;
        _conditions = (conditions ?? []).ToDictionary(c => c.Id);
        _log = log ?? (_ => { });
        _options = options ?? TriggerOptions.Default;
        foreach (var source in _sources)
            source.Raised += OnRaised;
    }

    /// <summary>Sources started now (tests, status).</summary>
    public int RunningSources
    {
        get
        {
            lock (_lock)
                return _started.Count;
        }
    }

    /// <summary>Takes the flows' event triggers; unchanged sources keep running, sources nobody needs stop.</summary>
    public void Apply(IEnumerable<Flow> runnableFlows)
    {
        ArgumentNullException.ThrowIfNull(runnableFlows);
        var known = _sources.SelectMany(s => s.Events).ToHashSet();
        Dictionary<string, List<Subscription>> table = [];
        foreach (var flow in runnableFlows)
        {
            for (var i = 0; i < flow.Triggers.Count; i++)
            {
                if (flow.Triggers[i] is not EventTrigger trigger || !TriggerText.IsEvent(trigger.Name))
                    continue;
                if (trigger.Only is { } only && !string.IsNullOrWhiteSpace(only) && !_conditions.ContainsKey(only))
                {
                    _log($"trigger: {flow.Id}: only \"{only}\" isn't a condition I know; skipped");
                    continue;
                }
                if (!known.Contains(trigger.Name))
                {
                    _log($"trigger: {flow.Id}: {trigger.Name} has no source in this build; skipped");
                    continue;
                }
                if (!table.TryGetValue(trigger.Name, out var list))
                    table[trigger.Name] = list = [];
                list.Add(new Subscription(flow.Id, i, trigger));
            }
        }

        List<ITriggerSource> toStart = [];
        List<ITriggerSource> toStop = [];
        lock (_lock)
        {
            if (_disposed)
                return;
            _byEvent = table;
            foreach (var source in _sources)
            {
                var wanted = source.Events.Any(table.ContainsKey);
                if (wanted && _started.Add(source))
                    toStart.Add(source);
                else if (!wanted && _started.Remove(source))
                    toStop.Add(source);
            }
            // A cooldown belongs to a trigger that still exists, unchanged (a replaced trigger at the same index starts fresh).
            var live = table.Values.SelectMany(l => l).ToHashSet();
            foreach (var key in _lastStart.Keys.Where(k => !live.Contains(k)).ToList())
                _lastStart.Remove(key);
        }
        // Outside the lock: a source may raise from inside Start/Stop.
        foreach (var source in toStop)
            Guarded(source, "stop", source.Unload);
        foreach (var source in toStart)
        {
            if (Guarded(source, "start", source.Load))
                continue;
            // A failed Load isn't running: forget it so the next Apply retries, and release whatever it half-registered.
            lock (_lock)
                _started.Remove(source);
            Guarded(source, "stop", source.Unload);
        }
    }

    /// <summary>Feeds an event in, as a source's <see cref="ITriggerSource.Raised"/> does.</summary>
    public void Raise(TriggerEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        List<Subscription> matches;
        lock (_lock)
        {
            if (_disposed || !_byEvent.TryGetValue(e.Name, out var subscriptions))
                return;
            if (_paused())
                return;
            var now = _time.GetTimestamp();
            // Debounce per name and detail: a connection to another Wi-Fi doesn't swallow the one a trigger waits for.
            var debounceKey = (e.Name, e.Detail);
            var seen = _lastEvent.TryGetValue(debounceKey, out var last);
            _lastEvent[debounceKey] = now;
            if (seen && _time.GetElapsedTime(last, now) < _options.Debounce)
                return;
            matches = [];
            foreach (var subscription in subscriptions)
            {
                // A Wi-Fi name on the trigger narrows network.connected; a name the source couldn't read matches none.
                if (subscription.Trigger.Ssid is { } ssid && !string.Equals(ssid, e.Detail, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (InCooldown(subscription, now))
                    continue;
                matches.Add(subscription);
            }
        }
        foreach (var subscription in matches)
        {
            var task = FireAsync(subscription, e);
            lock (_lock)
                _inFlight.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (_lock)
                    _inFlight.Remove(t);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Completes when the starts begun so far have been handed to the launcher (tests).</summary>
    public Task IdleAsync()
    {
        Task[] pending;
        lock (_lock)
            pending = [.. _inFlight];
        return Task.WhenAll(pending);
    }

    public void Dispose()
    {
        List<ITriggerSource> toStop;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _byEvent = [];
            toStop = [.. _started];
            _started.Clear();
        }
        _closed.Cancel();
        foreach (var source in _sources)
            source.Raised -= OnRaised;
        foreach (var source in toStop)
            Guarded(source, "stop", source.Unload);
        foreach (var source in _sources)
            Guarded(source, "dispose", source.Dispose);
        _closed.Dispose();
    }

    private void OnRaised(TriggerEvent e) => Raise(e);

    private async Task FireAsync(Subscription subscription, TriggerEvent e)
    {
        var flowId = subscription.FlowId;
        try
        {
            if (subscription.Trigger.Only is { } only && !string.IsNullOrWhiteSpace(only) && _conditions.TryGetValue(only, out var condition))
            {
                bool holds;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                    timeout.CancelAfter(_options.ConditionTimeout);
                    holds = await condition.HoldsAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception c) when (c is not OutOfMemoryException)
                {
                    if (_closed.IsCancellationRequested)
                        return;
                    holds = true; // unknown: the trigger fires anyway, like an "Only if" probe that can't read (D-081)
                    _log($"trigger: {flowId}: couldn't read {only} ({c.Message}); starting anyway");
                }
                if (!holds)
                {
                    _log($"trigger: {flowId}: {e.Name} ignored, {only} isn't true");
                    return;
                }
            }

            var called = _flows.Resolve(flowId);
            if (called.Flow is not { } flow)
            {
                _log($"trigger: {flowId}: {e.Name} not started: {called.Refusal}");
                return;
            }
            var chain = e.Chain ?? [];
            if (FlowCalls.Guard(chain, flowId, called.Name) is { } loop)
            {
                _log($"trigger: {flowId}: {e.Name} not started: {loop}");
                return;
            }
            // Admitted: reserve the cooldown now (atomically: a concurrent event for the same trigger loses), not before the
            // condition, flow and chain checks, so an event that started nothing doesn't eat the cooldown.
            lock (_lock)
            {
                var now = _time.GetTimestamp();
                if (InCooldown(subscription, now))
                    return;
                _lastStart[subscription] = now;
            }
            var text = TriggerText.Describe(subscription.Trigger);
            // The launcher applies whenRunning; this doesn't wait for the run, only observes a fault.
            var run = _launcher.TriggerAsync(flow, called.Library, text, chain);
            _ = run.ContinueWith(t => _log($"trigger: {flowId}: run failed: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            _log($"trigger: {flowId}: {e.Name} failed: {failure.Message}");
        }
    }

    /// <summary>Under the lock.</summary>
    private bool InCooldown(Subscription subscription, long now) =>
        _lastStart.TryGetValue(subscription, out var started) && _time.GetElapsedTime(started, now) < _options.Cooldown;

    private bool Guarded(ITriggerSource source, string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // One source's failure disables only itself (D-021).
            _log($"trigger: {source.GetType().Name} {what} failed: {failure.Message}");
            return false;
        }
    }

    private sealed record Subscription(string FlowId, int Index, EventTrigger Trigger);
}
