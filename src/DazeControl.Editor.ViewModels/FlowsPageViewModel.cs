using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels;

/// <summary>Flows page (UX §5.1): one card per flow in YAML order; a click opens it, Run runs it (D-070).</summary>
public sealed partial class FlowsPageViewModel : ObservableObject, IDisposable
{
    private const int HistoryLimit = 100;

    private readonly ConfigSession _session;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private Dictionary<string, RunRecord> _lastRuns = [];

    /// <summary>Live runs by id → flow, from the engine's events (UI thread).</summary>
    private readonly Dictionary<Guid, string> _liveRuns = [];

    public FlowsPageViewModel(ConfigSession session, IEngineClient engine, Action<Action> post, DesktopShortcutStore? desktop = null)
    {
        _session = session;
        _engine = engine;
        _post = post;
        _desktop = desktop ?? new DesktopShortcutStore(engine, session.Path);
        _session.PropertyChanged += OnSessionChanged;
        _engine.EventReceived += OnEngineEvent;
        _engine.StateChanged += OnEngineStateChanged;
        _desktop.Changed += OnDesktopChanged;
        Sync();
        _ = RefreshDesktopAsync();
    }

    private readonly DesktopShortcutStore _desktop;

    /// <summary>Why the last Add to desktop / Remove from desktop didn't work; null when it did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDesktopError))]
    public partial string? DesktopError { get; set; }

    public bool HasDesktopError => DesktopError is not null;

    /// <summary>The card's context menu (UX §5.4): adds the flow's desktop shortcut, or removes the one it has.</summary>
    public async Task ToggleDesktopAsync(string flowId)
    {
        DesktopError = null;
        var error = _desktop.Of(flowId) is null
            ? await _desktop.AddAsync(flowId).ConfigureAwait(false)
            : await _desktop.RemoveAsync(flowId).ConfigureAwait(false);
        _post(() => DesktopError = error);
    }

    private async Task RefreshDesktopAsync()
    {
        try
        {
            await _desktop.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // The engine went away mid-request: StateChanged follows.
        }
    }

    private void OnDesktopChanged() => _post(() =>
    {
        foreach (var item in Items)
            item.UpdateDesktop(_desktop.Of(item.Id) is not null, _desktop.EngineRunning);
    });

    public ObservableCollection<FlowItemViewModel> Items { get; } = [];

    /// <summary>Why the last Run from a card was refused; null when it wasn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunError))]
    public partial string? RunError { get; private set; }

    public bool HasRunError => RunError is not null;

    /// <summary>The card's Run: the saved version, trigger "Editor" (D-070). The run itself arrives as events.</summary>
    internal async Task RunAsync(string flowId)
    {
        RunError = null;
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Run, Flow = flowId, Trigger = "Editor" }).ConfigureAwait(false);
        if (answer is not { Ok: true })
            _post(() => RunError = answer?.Error ?? "The engine isn't running.");
    }

    internal async Task StopAsync(string flowId) =>
        await _engine.SendAsync(new PipeRequest { Op = Ops.Cancel, Flow = flowId }).ConfigureAwait(false);

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public bool CanEdit => _session.CanEdit;

    /// <summary>Asks the engine for the newest run of each flow.</summary>
    public async Task RefreshRunsAsync()
    {
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.History, Limit = HistoryLimit }).ConfigureAwait(false);
        if (answer?.History is not { } history)
            return;
        var last = new Dictionary<string, RunRecord>();
        foreach (var run in history)
        {
            if (!last.TryGetValue(run.FlowId, out var seen) || run.Started > seen.Started)
                last[run.FlowId] = run;
        }
        _post(() =>
        {
            _lastRuns = last;
            Sync();
        });
    }

    [RelayCommand]
    private void NewFlow()
    {
        _ = EndUndoAsync();
        var ids = _session.Current.Flows.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        _session.Update(c => c with { Flows = [.. c.Flows, new Flow(FlowIds.Unique("new-flow", ids), "New flow", [])] });
    }

    /// <summary>The flow Undo would put back, and where it was (D-076).</summary>
    private (Flow Flow, int Index)? _deleted;

    /// <summary>The Undo bar's text (#67); null when there's nothing to undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoDelete))]
    public partial string? DeletedText { get; private set; }

    public bool CanUndoDelete => DeletedText is not null;

    /// <summary>Why the flow can't be deleted now (#68), or null.</summary>
    internal string? DeleteOffReason(string flowId)
    {
        var callers = FlowRefs.Callers(_session.Current, flowId).Select(f => f.Name).ToList();
        return callers.Count switch
        {
            0 => null,
            1 => $"{callers[0]} runs this flow. Remove that step first.",
            _ => $"{string.Join(", ", callers[..^1])} and {callers[^1]} run this flow. Remove those steps first.",
        };
    }

    /// <summary>The card's Delete (D-076): the flow goes now (autosaved), and the bar offers Undo.</summary>
    public void Delete(string flowId)
    {
        if (!_session.CanEdit || DeleteOffReason(flowId) is not null)
            return;
        var index = _session.Current.Flows.ToList().FindIndex(f => f.Id == flowId);
        if (index < 0)
            return;
        _ = EndUndoAsync();
        var flow = _session.Current.Flows[index];
        _session.Update(c => c with { Flows = [.. c.Flows.Where(f => f.Id != flowId)] });
        _deleted = (flow, index);
        DeletedText = $"{flow.Name} was deleted.";
    }

    /// <summary>Undo: the flow back at its place, with its id.</summary>
    [RelayCommand]
    private void UndoDelete()
    {
        if (_deleted is not { } deleted)
            return;
        _deleted = null;
        DeletedText = null;
        if (_session.Current.Flows.Any(f => f.Id == deleted.Flow.Id))
            return; // the file got a flow with that id meanwhile
        _session.Update(c =>
        {
            var flows = c.Flows.ToList();
            flows.Insert(Math.Min(deleted.Index, flows.Count), deleted.Flow);
            return c with { Flows = [.. flows] };
        });
    }

    /// <summary>
    /// The chance to undo ends (another Delete, New flow, opening a flow, leaving the page, closing the editor): only now
    /// does the deleted flow's desktop shortcut go.
    /// </summary>
    public Task EndUndoAsync()
    {
        if (_deleted is not { } deleted)
            return Task.CompletedTask;
        _deleted = null;
        DeletedText = null;
        // Asked of the engine, not the cached list: a shortcut made by dctl or another editor counts too.
        return !_session.Current.Flows.Any(f => f.Id == deleted.Flow.Id)
            ? RemoveShortcutAsync(deleted.Flow.Id)
            : Task.CompletedTask;
    }

    private async Task RemoveShortcutAsync(string flowId)
    {
        try
        {
            var error = await _desktop.RemoveAsync(flowId).ConfigureAwait(false);
            if (error is not null)
                _post(() => DesktopError = error);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // The engine went away: the shortcut stays and says its flow is gone when used (#25 family).
        }
    }

    internal void SetEnabled(string flowId, bool enabled) =>
        _session.Update(c => c with
        {
            Flows = [.. c.Flows.Select(f => f.Id == flowId && f.Enabled != enabled ? f with { Enabled = enabled } : f)],
        });

    /// <summary>Updates the cards in place, so a toggle being clicked keeps its focus.</summary>
    private void Sync()
    {
        var flows = _session.Current.Flows;
        var withErrors = _session.Problems.Where(p => p.Severity == Severity.Error && p.FlowId is not null)
            .GroupBy(p => p.FlowId!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!flows.Any(f => f.Id == Items[i].Id))
                Items.RemoveAt(i);
        }
        for (var i = 0; i < flows.Count; i++)
        {
            var flow = flows[i];
            var existing = Items.FirstOrDefault(item => item.Id == flow.Id);
            if (existing is null)
            {
                existing = new FlowItemViewModel(this, flow.Id);
                existing.UpdateDesktop(_desktop.Of(flow.Id) is not null, _desktop.EngineRunning);
                Items.Insert(i, existing);
            }
            else if (Items.IndexOf(existing) != i)
            {
                Items.Move(Items.IndexOf(existing), i);
            }
            existing.Update(flow, _lastRuns.GetValueOrDefault(flow.Id), withErrors.GetValueOrDefault(flow.Id), _session.CanEdit);
            existing.UpdateDelete(_session.CanEdit, DeleteOffReason(flow.Id));
            existing.UpdateRun(_engine.IsRunning, _liveRuns.ContainsValue(flow.Id));
        }
        IsEmpty = Items.Count == 0;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfigSession.Current) or nameof(ConfigSession.Problems))
            Sync();
        if (e.PropertyName == nameof(ConfigSession.CanEdit))
        {
            OnPropertyChanged(nameof(CanEdit));
            Sync();
        }
    }

    private void OnEngineEvent(PipeEvent e)
    {
        if (e is { RunId: { } runId, FlowId: { } flowId })
        {
            // A run that started before the editor opened shows from its next step on.
            if (e.Kind is EventKinds.RunStarted or EventKinds.NodeStarted)
                _post(() => SetLive(runId, flowId, live: true));
            else if (e.Kind == EventKinds.RunFinished)
                _post(() => SetLive(runId, flowId, live: false));
        }
        if (e.Kind == EventKinds.RunFinished)
            _ = RefreshRunsAsync();
    }

    private void SetLive(Guid runId, string flowId, bool live)
    {
        if (live)
            _liveRuns[runId] = flowId;
        else
            _liveRuns.Remove(runId);
        UpdateRuns();
    }

    private void UpdateRuns(bool? engineRunning = null)
    {
        var running = engineRunning ?? _engine.IsRunning;
        foreach (var item in Items)
            item.UpdateRun(running, _liveRuns.ContainsValue(item.Id));
    }

    private void OnEngineStateChanged()
    {
        if (_engine.IsRunning)
            _ = RefreshRunsAsync();
        // Read now: by the time the UI thread runs this, the engine may be back, and its runs still went with it.
        var running = _engine.IsRunning;
        _post(() =>
        {
            if (!running)
                _liveRuns.Clear();
            UpdateRuns(running);
        });
        _ = RefreshDesktopAsync();
    }

    public void Dispose()
    {
        _ = EndUndoAsync();
        _session.PropertyChanged -= OnSessionChanged;
        _engine.EventReceived -= OnEngineEvent;
        _engine.StateChanged -= OnEngineStateChanged;
        _desktop.Changed -= OnDesktopChanged;
    }
}

public sealed partial class FlowItemViewModel : ObservableObject
{
    /// <summary>Segoe Fluent "Lightning bolt" when the flow names no glyph.</summary>
    public const string DefaultGlyph = FlowGlyph.Default;

    private readonly FlowsPageViewModel _page;
    private bool _syncing;

    internal FlowItemViewModel(FlowsPageViewModel page, string id)
    {
        _page = page;
        Id = id;
    }

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenName), nameof(RunName))]
    public partial string Name { get; private set; } = "";

    /// <summary>The card's UIA name: it opens the flow on Enter/Space (D-070).</summary>
    public string OpenName => $"Open {Name}";

    /// <summary>The Run button's UIA name.</summary>
    public string RunName => $"{RunText} {Name}";

    /// <summary>"Stop" while this flow runs.</summary>
    public string RunText => IsRunning ? "Stop" : "Run";

    /// <summary>A run of this flow is going on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText), nameof(RunName), nameof(RunTip))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunOffReason), nameof(CanRun), nameof(RunTip))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool EngineRunning { get; private set; }

    private int _errors;

    /// <summary>Why Run is off (#62): engine, then the On switch, then problems; null when it can run.</summary>
    public string? RunOffReason =>
        !EngineRunning ? "Start Daze Control to run flows."
        : !IsEnabled ? "Turn it on to run it."
        : _errors > 0 ? "Fix its problems to run it."
        : null;

    public bool CanRun => RunOffReason is null;

    /// <summary>The Run button's tooltip: the reason it's off, else what it does.</summary>
    public string RunTip => IsRunning ? "Stop this run" : RunOffReason ?? "Run the saved version";

    /// <summary>Run, or Stop while it runs (Stop needs only the engine).</summary>
    private bool CanRunOrStop => IsRunning ? EngineRunning : CanRun;

    // Off while its request is out, so a double click sends one run.
    [RelayCommand(CanExecute = nameof(CanRunOrStop))]
    private Task RunAsync() => IsRunning ? _page.StopAsync(Id) : _page.RunAsync(Id);

    internal void UpdateRun(bool engineRunning, bool running)
    {
        EngineRunning = engineRunning;
        IsRunning = running;
    }

    [ObservableProperty]
    public partial string Glyph { get; private set; } = DefaultGlyph;

    [ObservableProperty]
    public partial string Shortcut { get; private set; } = "";

    [ObservableProperty]
    public partial string LastRun { get; private set; } = "";

    [ObservableProperty]
    public partial string Problems { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasProblems { get; private set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DesktopMenuText))]
    public partial bool HasDesktopShortcut { get; private set; }

    /// <summary>The context menu's item: "Add to desktop" (#23), "Remove from desktop", or #25 (off) without the engine.</summary>
    public string DesktopMenuText => !CanChangeDesktop ? DesktopShortcutStore.EngineOffText
        : HasDesktopShortcut ? "Remove from desktop" : DesktopShortcutStore.AddText;

    /// <summary>Only the engine writes shortcuts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DesktopMenuText))]
    public partial bool CanChangeDesktop { get; private set; }

    internal void UpdateDesktop(bool hasShortcut, bool engineRunning)
    {
        HasDesktopShortcut = hasShortcut;
        CanChangeDesktop = engineRunning;
    }

    /// <summary>The menu's Delete is on.</summary>
    [ObservableProperty]
    public partial bool CanDelete { get; private set; }

    /// <summary>#68 under Delete while another flow runs this one; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeleteOffReason))]
    public partial string? DeleteOffReason { get; private set; }

    public bool HasDeleteOffReason => DeleteOffReason is not null;

    internal void UpdateDelete(bool canEdit, string? offReason)
    {
        DeleteOffReason = offReason;
        CanDelete = canEdit && offReason is null;
    }

    /// <summary>False while the file is read-only for the editor.</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    internal void Update(Flow flow, RunRecord? lastRun, int errors, bool canEdit)
    {
        _syncing = true;
        try
        {
            Name = flow.Name;
            Glyph = FlowGlyph.Decode(FlowGlyph.CodeOf(flow));
            CanEdit = canEdit;
            Shortcut = flow.Hotkeys.Count == 0 ? "No shortcut" : string.Join(", ", flow.Hotkeys);
            LastRun = lastRun is null ? "Not run yet" : $"{Runs.RunWords.Chip(lastRun.Outcome)} · {lastRun.Started.ToLocalTime():g}";
            HasProblems = errors > 0;
            Problems = errors switch { 0 => "", 1 => "1 problem", _ => $"{errors} problems" };
            IsEnabled = flow.Enabled;
            if (_errors != errors)
            {
                _errors = errors;
                OnPropertyChanged(nameof(RunOffReason));
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(RunTip));
                RunCommand.NotifyCanExecuteChanged();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>YAML holds the code point in hex ("E7F4"); anything else falls back to the default.</summary>
    internal static string DecodeGlyph(string? code) => FlowGlyph.Decode(code);

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(RunOffReason));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunTip));
        RunCommand.NotifyCanExecuteChanged();
        if (!_syncing)
            _page.SetEnabled(Id, value);
    }
}
