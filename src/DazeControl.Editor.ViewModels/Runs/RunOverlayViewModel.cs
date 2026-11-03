using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Runs;

/// <summary>Maps to the WinUI InfoBarSeverity.</summary>
public enum RunInfoSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Runs on the canvas (UX §5.5): Run / Dry run / Stop in the CommandBar, a live run's states from the engine's
/// events (any trigger, not only this window's), "Running · 2 of 4", editing locked while it runs, and a finished
/// or recorded run shown until the InfoBar is closed.
/// </summary>
public sealed partial class RunOverlayViewModel : ObservableObject, IDisposable
{
    private readonly FlowCanvasViewModel _canvas;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, NodeRunView> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _steps = new(StringComparer.Ordinal);
    private Guid? _runId;
    private int _done;

    public RunOverlayViewModel(FlowCanvasViewModel canvas, IEngineClient engine, Action<Action> post)
    {
        _canvas = canvas;
        _engine = engine;
        _post = post;
        _engine.EventReceived += OnEngineEvent;
        _engine.StateChanged += OnEngineStateChanged;
    }

    /// <summary>A live run of this flow is on the canvas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanStop))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(DryRunCommand), nameof(StopCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>A dry run request is out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(DryRunCommand))]
    public partial bool IsBusy { get; private set; }

    public bool EngineRunning => _engine.IsRunning;

    public bool CanRun => EngineRunning && !IsRunning && !IsBusy && !_canvas.IsMissing;

    public bool CanStop => EngineRunning && IsRunning;

    /// <summary>"Running · 2 of 4".</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsInfoOpen { get; private set; }

    [ObservableProperty]
    public partial string InfoTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string InfoMessage { get; private set; } = "";

    [ObservableProperty]
    public partial RunInfoSeverity InfoSeverity { get; private set; }

    /// <summary>The InfoBar can be closed (not while the run that locks editing goes on).</summary>
    public bool CanCloseInfo => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        try
        {
            var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Run, Flow = _canvas.FlowId, Trigger = "Editor" });
            // The run itself arrives as events; only a refusal needs saying here.
            if (answer is not { Ok: true })
                Info(RunInfoSeverity.Error, "Couldn't run it", answer?.Error ?? "The engine isn't running.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Walks the flow as it is on screen, saved or not; steps do nothing (UX §5.5).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DryRunAsync()
    {
        if (_canvas.Flow is not { } flow)
            return;
        var yaml = ConfigYaml.Save(new Configuration { Library = _canvas.Library, Flows = [flow] });
        IsBusy = true;
        PipeMessage? answer;
        try
        {
            answer = await _engine.SendAsync(new PipeRequest
            {
                Op = Ops.Run, Flow = flow.Id, DryRun = true, Wait = true, Config = yaml, Trigger = "Editor",
            });
        }
        finally
        {
            IsBusy = false;
        }
        if (IsRunning)
            return; // a hotkey started it meanwhile: the live run owns the canvas
        if (answer is not { Ok: true, Run: { } run })
        {
            Info(RunInfoSeverity.Error, "Couldn't do a dry run", answer?.Error ?? "The engine isn't running.");
            return;
        }
        ShowDryRun(run);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync() =>
        await _engine.SendAsync(new PipeRequest { Op = Ops.Cancel, Flow = _canvas.FlowId });

    /// <summary>"Open in editor" from the Runs page: that run's states and durations on the canvas.</summary>
    public void Show(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (IsRunning)
            return;
        _states.Clear();
        foreach (var node in run.Nodes.Where(IsStep))
            _states[node.NodeId] = RunWords.View(node);
        _canvas.ShowRun(new Dictionary<string, NodeRunView>(_states));
        Info(Severity(run.Outcome), RunWords.Chip(run.Outcome),
            $"{RunWords.Sentence(run, Title)} {run.Trigger}, {run.Started.ToLocalTime():g}.");
    }

    /// <summary>Closing the InfoBar puts the canvas back to editing.</summary>
    public void CloseInfo()
    {
        if (IsRunning)
            return;
        IsInfoOpen = false;
        _states.Clear();
        _canvas.ShowRun(null);
    }

    internal void ShowDryRun(RunRecord run)
    {
        _states.Clear();
        foreach (var node in run.Nodes.Where(IsStep))
        {
            var view = RunWords.View(node, showDuration: false, node.Order);
            if (node.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored && node.Error is { } error)
                view = view with { Text = RunWords.FirstLine(error) };
            _states[node.NodeId] = view;
        }
        _canvas.ShowRun(new Dictionary<string, NodeRunView>(_states));
        var started = run.Nodes.Count(n => n.Order > 0 && IsStep(n));
        var failed = run.Nodes.Where(IsStep).Where(n => n.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored).ToList();
        var skipped = run.Nodes.Where(IsStep).Count(n => n.Outcome is NodeOutcome.Skipped or NodeOutcome.Cancelled);
        var parts = new List<string> { $"{Steps(started)} would start, in the order shown." };
        if (failed.Count > 0)
            parts.Add($"{Title(failed[0].NodeId)} would fail: {RunWords.FirstLine(failed[0].Error ?? "")}");
        if (skipped > 0)
            parts.Add($"{Steps(skipped)} wouldn't run.");
        Info(failed.Count > 0 || skipped > 0 ? RunInfoSeverity.Warning : RunInfoSeverity.Success, "Dry run", string.Join(" ", parts));
    }

    private static string Steps(int n) => n == 1 ? "1 step" : $"{n} steps";

    // ---- Live run (events arrive on any thread) ----

    private void OnEngineEvent(PipeEvent e)
    {
        if (e.FlowId == _canvas.FlowId && e.RunId is { } runId)
            _post(() => Apply(e, runId));
    }

    internal void Apply(PipeEvent e, Guid runId)
    {
        if (e.Kind == EventKinds.RunStarted || (e.Kind == EventKinds.NodeStarted && _runId is null))
            Begin(runId); // a run that started before this window opened shows from its next step on
        if (runId != _runId)
            return;
        switch (e.Kind)
        {
            case EventKinds.NodeStarted when e.NodeId is { } id && _steps.Contains(id):
                _states[id] = new NodeRunView(NodeRunState.Running, "Running…");
                break;
            case EventKinds.NodeFinished when e.Node is { } node && _steps.Contains(node.NodeId):
                _states[node.NodeId] = RunWords.View(node);
                _done++;
                break;
            case EventKinds.RunFinished:
                Finish(e.Outcome ?? FlowOutcome.Failed);
                return;
            default:
                return;
        }
        Update();
    }

    private void Begin(Guid runId)
    {
        _runId = runId;
        _states.Clear();
        _steps.Clear();
        _done = 0;
        // A live run executes the saved flow, not the draft on screen (D-047).
        foreach (var step in (_canvas.SavedFlow ?? _canvas.Flow)?.Nodes.OfType<StepNode>() ?? [])
        {
            _steps.Add(step.Id);
            _states[step.Id] = new NodeRunView(NodeRunState.Pending);
        }
        IsRunning = true;
        _canvas.IsRunLocked = true;
        Info(RunInfoSeverity.Informational, "Running", "You can edit again when this run finishes.");
        OnPropertyChanged(nameof(CanCloseInfo));
        Update();
    }

    private void Update()
    {
        Status = IsRunning ? $"Running · {_done} of {_steps.Count}" : "";
        _canvas.ShowRun(new Dictionary<string, NodeRunView>(_states));
    }

    private void Finish(FlowOutcome outcome)
    {
        // Steps the run never reported (a stop) stay "Pending" otherwise.
        foreach (var id in _states.Where(s => s.Value.State is NodeRunState.Pending or NodeRunState.Running).Select(s => s.Key).ToList())
            _states[id] = new NodeRunView(NodeRunState.Skipped, "Stopped");
        _runId = null;
        IsRunning = false;
        _canvas.IsRunLocked = false;
        Update();
        var failed = _states.FirstOrDefault(s => s.Value.State is NodeRunState.Failed or NodeRunState.FailedMayFail).Key;
        var name = _canvas.FlowName;
        var message = outcome switch
        {
            FlowOutcome.Succeeded => $"{name} is done.",
            FlowOutcome.PartiallySucceeded when failed is not null => $"Done, with a problem. {Title(failed)} didn't work, so it went on without it.",
            FlowOutcome.PartiallySucceeded => "Done, with a problem. Some steps were skipped.",
            FlowOutcome.Failed when failed is not null => $"{name} didn't finish. {Title(failed)} didn't work.",
            FlowOutcome.Failed => $"{name} didn't finish.",
            _ => $"{name} was stopped.",
        };
        Info(Severity(outcome), RunWords.Chip(outcome), message);
        OnPropertyChanged(nameof(CanCloseInfo));
    }

    private void OnEngineStateChanged() => _post(() =>
    {
        OnPropertyChanged(nameof(EngineRunning));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanStop));
        RunCommand.NotifyCanExecuteChanged();
        DryRunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        if (!_engine.IsRunning && IsRunning)
        {
            // Fail-soft: never leave the editor locked behind a run nobody will finish reporting.
            _runId = null;
            IsRunning = false;
            _canvas.IsRunLocked = false;
            Update();
            Info(RunInfoSeverity.Warning, "Run unknown", "The engine stopped, so how the run ended isn't known.");
            OnPropertyChanged(nameof(CanCloseInfo));
        }
    });

    private void Info(RunInfoSeverity severity, string title, string message)
    {
        InfoSeverity = severity;
        InfoTitle = title;
        // "Done, with a problem" is the title already: the message goes straight to what happened.
        InfoMessage = message.StartsWith(title + ". ", StringComparison.Ordinal) ? message[(title.Length + 2)..] : message;
        IsInfoOpen = true;
    }

    private static RunInfoSeverity Severity(FlowOutcome outcome) => outcome switch
    {
        FlowOutcome.Succeeded => RunInfoSeverity.Success,
        FlowOutcome.PartiallySucceeded => RunInfoSeverity.Warning,
        FlowOutcome.Failed => RunInfoSeverity.Error,
        _ => RunInfoSeverity.Informational,
    };

    private bool IsStep(NodeResult node) => _canvas.Flow?.FindNode(node.NodeId) is StepNode;

    private string Title(string id) => _canvas.Find(id)?.Title ?? id;

    public void Dispose()
    {
        _engine.EventReceived -= OnEngineEvent;
        _engine.StateChanged -= OnEngineStateChanged;
        if (IsRunning)
            _canvas.IsRunLocked = false;
    }
}
