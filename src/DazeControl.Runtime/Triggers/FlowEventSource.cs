using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime.Triggers;

/// <summary>
/// <c>flow.finished</c> / <c>flow.failed</c> (T26.3): raised in this process when a run started through the
/// <see cref="FlowLauncher"/> ends. Nothing is held until some flow subscribes (<see cref="Load"/> hooks the launcher's
/// <see cref="FlowLauncher.RunCompleted"/>; <see cref="Unload"/> unhooks it): no timer, thread or handle (D-009).
/// "Finished" = ran to its end (all steps fine, or some failed or were skipped); "failed" = the run failed. A stopped or
/// cancelled run, and a run nested inside another with a waiting <c>flow.run</c> (it never goes through the launcher), raise
/// nothing. The event carries the ended flow's id (<see cref="TriggerEvent.Kind"/>), its name (<see cref="TriggerEvent.Detail"/>)
/// and the run's chain plus the flow itself, so <see cref="Control.FlowCalls.Guard"/> stops a flow that would start itself.
/// </summary>
public sealed class FlowEventSource(FlowLauncher launcher) : ITriggerSource
{
    public static readonly IReadOnlyList<string> Names = ["flow.finished", "flow.failed"];

    private readonly Lock _lock = new();
    private bool _loaded;
    private bool _disposed;

    public IReadOnlyList<string> Events => Names;

    public event Action<TriggerEvent>? Raised;

    public void Load()
    {
        lock (_lock)
        {
            if (_loaded || _disposed)
                return;
            _loaded = true;
            launcher.RunCompleted += OnCompleted;
        }
    }

    public void Unload()
    {
        lock (_lock)
        {
            if (!_loaded)
                return;
            _loaded = false;
            launcher.RunCompleted -= OnCompleted;
        }
    }

    public void Dispose()
    {
        Unload();
        lock (_lock)
            _disposed = true;
    }

    /// <summary>The event a run's end raises, or null when it raises none (stopped, cancelled).</summary>
    internal static TriggerEvent? EventFor(RunFinished finished) => finished.Outcome switch
    {
        FlowOutcome.Succeeded or FlowOutcome.PartiallySucceeded => Make("flow.finished", finished),
        FlowOutcome.Failed => Make("flow.failed", finished),
        _ => null,
    };

    private static TriggerEvent Make(string name, RunFinished finished) =>
        new(name, [.. finished.Chain, finished.FlowId], finished.FlowName ?? finished.FlowId, finished.FlowId);

    private void OnCompleted(RunFinished finished)
    {
        bool loaded;
        lock (_lock)
            loaded = _loaded;
        // A handler already running while Unload ran raises an event the service has no subscriber for any more.
        if (loaded && EventFor(finished) is { } e)
            Raised?.Invoke(e);
    }
}
