using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Editor.ViewModels.FlowList;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels;

/// <summary>Flows page (UX §5.1): one card per flow in YAML order; a click opens it, Run runs it (D-070).</summary>
/// <summary>The "Show" filter of the command bar (DESIGN Part E).</summary>
public enum FlowFilter
{
    All,
    On,
    Off,
    NeedsFixing,
    NotInTray,
}

public sealed partial class FlowsPageViewModel : ObservableObject, IDisposable
{
    private const int HistoryLimit = 100; // RunHistory.Capacity: the engine keeps (and answers with) 100 runs, not the file's 200 lines (D-089)

    private readonly ConfigSession _session;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private Dictionary<string, RunRecord> _lastRuns = [];

    /// <summary>Live runs by id → flow, from the engine's events (UI thread).</summary>
    private readonly Dictionary<Guid, string> _liveRuns = [];

    public FlowsPageViewModel(ConfigSession session, IEngineClient engine, Action<Action> post, DesktopShortcutStore? desktop = null, TimeProvider? time = null, EditorSettingsStore? settings = null)
    {
        _time = time ?? TimeProvider.System;
        _settings = settings;
        foreach (var name in settings?.Settings.FlowList.CollapsedGroups ?? [])
            _collapsed.Add(name);
        _sort = Enum.TryParse<FlowSort>(settings?.Settings.FlowList.Sort, out var sort) && Enum.IsDefined(sort) ? sort : FlowSort.Yours;
        _filter = Enum.TryParse<FlowFilter>(settings?.Settings.FlowList.Filter, out var filter) && Enum.IsDefined(filter) ? filter : FlowFilter.All;
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

    /// <summary>Every flow in file order, one view model each (the list shows them through <see cref="Rows"/>).</summary>
    public ObservableCollection<FlowItemViewModel> Items { get; } = [];

    /// <summary>
    /// The list as shown (D-087): group headers (<see cref="GroupItemViewModel"/>) and flows (<see cref="FlowItemViewModel"/>)
    /// in tray order; the members of a collapsed group are left out.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    private readonly EditorSettingsStore? _settings;
    private readonly HashSet<string> _collapsed = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<string, GroupItemViewModel> _groups = new(StringComparer.CurrentCultureIgnoreCase);

    private FlowSort _sort;
    private FlowFilter _filter;
    private string _search = "";

    /// <summary>Your order (the file / tray order), Name or Last run; view-only, remembered per PC (D-088).</summary>
    public FlowSort Sort
    {
        get => _sort;
        set
        {
            if (_sort == value)
                return;
            _sort = value;
            _settings?.Update(s => s.FlowList.Sort = value == FlowSort.Yours ? null : value.ToString());
            ViewChanged(nameof(Sort));
        }
    }

    /// <summary>All, On, Off, Needs fixing or Not in tray; remembered per PC.</summary>
    public FlowFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
                return;
            _filter = value;
            _settings?.Update(s => s.FlowList.Filter = value == FlowFilter.All ? null : value.ToString());
            ViewChanged(nameof(Filter));
        }
    }

    /// <summary>The combo boxes' index (the enums' order).</summary>
    public int SortIndex
    {
        get => (int)_sort;
        set
        {
            if (value >= 0)
                Sort = (FlowSort)value;
        }
    }

    public int FilterIndex
    {
        get => (int)_filter;
        set
        {
            if (value >= 0)
                Filter = (FlowFilter)value;
        }
    }

    /// <summary>The search box; not remembered.</summary>
    public string SearchText
    {
        get => _search;
        set
        {
            value ??= "";
            if (_search == value)
                return;
            _search = value;
            ViewChanged(nameof(SearchText));
        }
    }

    private bool Searching => _search.Trim().Length > 0;

    /// <summary>A search or a filter hides rows.</summary>
    public bool IsFiltering => Searching || _filter != FlowFilter.All;

    /// <summary>Anything but plain Your order with nothing hidden.</summary>
    public bool ViewActive => IsFiltering || _sort != FlowSort.Yours;

    /// <summary>Drag, Alt+arrows and Move to rearrange the file, so they need the file's own order on screen (D-088).</summary>
    public bool CanReorder => _session.CanEdit && !ViewActive;

    /// <summary>Why reordering is off because of the view; null when it isn't.</summary>
    public string? ReorderOffReason => !_session.CanEdit ? null
        : _sort != FlowSort.Yours ? $"Sorted by {(_sort == FlowSort.Name ? "name" : "last run")}. Switch to Your order to rearrange."
        : IsFiltering ? "Clear the search and filter to rearrange." : null;

    /// <summary>"Make this my order" is offered while a sort is on.</summary>
    public bool CanMakeOrder => _session.CanEdit && _sort != FlowSort.Yours;

    /// <summary>The search or filter hid every flow.</summary>
    public bool NoMatches => Items.Count > 0 && Rows.Count == 0;

    private void ViewChanged(string name)
    {
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(SortIndex));
        OnPropertyChanged(nameof(FilterIndex));
        OnPropertyChanged(nameof(IsFiltering));
        OnPropertyChanged(nameof(ViewActive));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(ReorderOffReason));
        OnPropertyChanged(nameof(CanMakeOrder));
        MakeOrderCommand.NotifyCanExecuteChanged();
        Sync();
    }

    /// <summary>The empty state's Clear: no search, no filter (the sort stays).</summary>
    [RelayCommand]
    private void ClearFilter()
    {
        Filter = FlowFilter.All;
        SearchText = "";
    }

    /// <summary>"Make this my order": the visible sorted order becomes the file's (one Undo), then the view is Your order.</summary>
    [RelayCommand(CanExecute = nameof(CanMakeOrder))]
    private void MakeOrder()
    {
        var sort = _sort;
        if (!CanMakeOrder)
            return;
        var map = _lastRuns.ToDictionary(r => r.Key, r => r.Value.Started, StringComparer.Ordinal);
        Commit(c => FlowListModel.MakeOrder(c, sort, id => map.TryGetValue(id, out var t) ? t : null));
        Sort = FlowSort.Yours;
    }

    private DateTimeOffset? LastRunOf(string id) => _lastRuns.TryGetValue(id, out var r) ? r.Started : null;

    private bool Matches(FlowItemViewModel item)
    {
        var ok = _filter switch
        {
            FlowFilter.On => item.IsEnabled,
            FlowFilter.Off => !item.IsEnabled,
            FlowFilter.NeedsFixing => item.HasProblems,
            FlowFilter.NotInTray => item.NotInTray,
            _ => true,
        };
        return ok && (!Searching || item.SearchHaystack.Contains(_search.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>The groups in list order, for the "Move to" menu.</summary>
    public IReadOnlyList<string> GroupNames => [.. FlowListModel.Rows(_session.Current).OfType<GroupRow>().Select(g => g.Name)];

    /// <summary>Expands or collapses a group; remembered per PC (D-068).</summary>
    public void ToggleGroup(string name)
    {
        if (!_collapsed.Remove(name))
            _collapsed.Add(name);
        SaveCollapsed();
        SyncRows();
    }

    private void SaveCollapsed() => _settings?.Update(s => s.FlowList.CollapsedGroups = [.. _collapsed.Order(StringComparer.CurrentCultureIgnoreCase)]);

    private void ForgetCollapsed(string name, string? becomes = null)
    {
        if (!_collapsed.Remove(name))
            return;
        if (becomes is not null)
            _collapsed.Add(becomes);
        SaveCollapsed();
    }

    private void Apply(Func<Configuration, Configuration> change)
    {
        if (_session.CanEdit)
            Commit(change);
    }

    private const int UndoDepth = 20;
    private readonly List<Configuration> _undo = [];
    private int _deleteMark = -1;
    private bool _committing;

    /// <summary>Anything to undo on the list (DESIGN B4)?</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>
    /// Every change this page makes goes through here: the previous version is pushed on the Undo stack (20), unless the
    /// change leaves the order, groups and flows as they were.
    /// </summary>
    private void Commit(Func<Configuration, Configuration> change)
    {
        var before = _session.Current;
        _committing = true;
        try
        {
            _session.Update(change);
        }
        finally
        {
            _committing = false;
        }
        if (ReferenceEquals(before, _session.Current))
            return;
        _undo.Add(before);
        if (_undo.Count > UndoDepth)
            _undo.RemoveAt(0);
        OnPropertyChanged(nameof(CanUndo));
        UndoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Forgets the Undo stack: opening a flow, leaving the page, closing, or a change that arrived from outside.</summary>
    public void ClearUndo()
    {
        _deleteMark = -1;
        if (_undo.Count == 0)
            return;
        _undo.Clear();
        OnPropertyChanged(nameof(CanUndo));
        UndoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Puts back the list as <paramref name="previous"/> had it (order, groups, On/Off, flows that were deleted or added) but
    /// keeps every other part of the flows that are still there, so an open flow's unsaved draft stays a draft.
    /// </summary>
    private static Configuration Restore(Configuration current, Configuration previous) => current with
    {
        Flows = [.. previous.Flows.Select(p => current.Flows.FirstOrDefault(f => f.Id == p.Id) is { } now
            ? now with { Enabled = p.Enabled, Tray = now.Tray with { Folder = p.Tray.Folder } }
            : p)],
    };

    /// <summary>Undo / Ctrl+Z: the list as it was before the last change.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0 || !_session.CanEdit)
            return;
        var previous = _undo[^1];
        if (_deleted is not null && _undo.Count == _deleteMark)
        {
            _deleted = null; // this step is the delete's own undo
            DeletedText = null;
        }
        _undo.RemoveAt(_undo.Count - 1);
        _committing = true; // not pushed again, not "from outside"
        try
        {
            _session.Update(c => Restore(c, previous));
        }
        finally
        {
            _committing = false;
        }
        OnPropertyChanged(nameof(CanUndo));
        UndoCommand.NotifyCanExecuteChanged();
    }

    public void RenameGroup(string name, string newName)
    {
        if (!FlowListModel.IsValidGroupName(newName))
            return;
        ForgetCollapsed(name, newName.Trim());
        Apply(c => FlowListModel.RenameGroup(c, name, newName));
    }

    public void Ungroup(string name)
    {
        ForgetCollapsed(name);
        Apply(c => FlowListModel.Ungroup(c, name));
    }

    /// <summary>The group menu's Turn all on / off.</summary>
    public void SetGroupEnabled(string name, bool enabled) => Apply(c => c with
    {
        Flows = [.. c.Flows.Select(f => StringComparer.CurrentCultureIgnoreCase.Equals(f.Tray.Folder, name) && f.Enabled != enabled ? f with { Enabled = enabled } : f)],
    });

    /// <summary>The group menu's Move up / down: one place among the top-level items.</summary>
    public void MoveGroup(string name, int delta)
    {
        if (!CanReorder)
            return;
        var units = TopLevelUnits(_session.Current);
        var at = units.FindIndex(u => u.Group is not null && StringComparer.CurrentCultureIgnoreCase.Equals(u.Group, name));
        var to = at + delta;
        if (at < 0 || to < 0 || to >= units.Count)
            return;
        var before = delta < 0 ? units[to].FirstId : to + 1 < units.Count ? units[to + 1].FirstId : null;
        Apply(c => FlowListModel.MoveGroup(c, name, before));
    }

    /// <summary>"Move to ›": into a group (created when new), or out of its group when <paramref name="group"/> is null.</summary>
    public void MoveFlowToGroup(string flowId, string? group)
    {
        if (group is not null && !FlowListModel.IsValidGroupName(group))
            return;
        if (!CanReorder)
            return;
        var current = Items.FirstOrDefault(i => i.Id == flowId)?.Group;
        if (group is null ? current is null : current is not null && StringComparer.CurrentCultureIgnoreCase.Equals(group.Trim(), current))
            return; // already there: no reorder, no expanding
        if (group is not null && _collapsed.Remove(group)) // a flow moved in shows
            SaveCollapsed();
        Apply(c => FlowListModel.SetGroup(c, [flowId], group));
    }

    /// <summary>The visible rows for <see cref="DropMath"/>, in list order.</summary>
    public IReadOnlyList<DropRow> DropRows => [.. Rows.Select(r => r switch
    {
        GroupItemViewModel g => new DropRow(true, g.Name, null, g.FirstMemberId),
        FlowItemViewModel f => new DropRow(false, f.Id, f.Group),
        _ => default,
    })];

    /// <summary>A flow dropped (DESIGN B2): into <paramref name="group"/> (null = top level) before <paramref name="beforeId"/>.</summary>
    public void DropFlow(string flowId, string? group, string? beforeId)
    {
        if (beforeId == flowId || !CanReorder)
            return; // dropped on itself, or the view isn't Your order
        if (group is not null && _collapsed.Remove(group)) // dropped in: it shows
            SaveCollapsed();
        ApplyOrder(c => FlowListModel.Move(c, [flowId], group, beforeId));
    }

    /// <summary>A group dropped as a block before the top-level item holding <paramref name="beforeId"/> (null = at the end).</summary>
    public void DropGroup(string name, string? beforeId) => ApplyOrder(c => FlowListModel.MoveGroup(c, name, beforeId));

    /// <summary>A reorder that lands where it started (a drop on itself) changes nothing and is no Undo step.</summary>
    private void ApplyOrder(Func<Configuration, Configuration> change)
    {
        if (!CanReorder)
            return;
        var next = change(_session.Current);
        if (Shape(next) != Shape(_session.Current))
            Commit(change); // the pure change, so the session applies it to the saved version and to the drafts alike
    }

    private static string Shape(Configuration c) => string.Join(';', c.Flows.Select(f => $"{f.Id}|{f.Tray.Folder}"));

    /// <summary>Alt+Up / Alt+Down and the menu's Move up / down: one place within the flow's container (its group, or the top level).</summary>
    public void MoveFlow(string flowId, int delta)
    {
        if (!CanReorder)
            return;
        var group = Items.FirstOrDefault(i => i.Id == flowId)?.Group;
        if (group is not null)
        {
            var members = FlowListModel.Rows(_session.Current).OfType<GroupRow>()
                .FirstOrDefault(g => StringComparer.CurrentCultureIgnoreCase.Equals(g.Name, group))?.FlowIds ?? [];
            var at = members.ToList().IndexOf(flowId);
            var to = at + delta;
            if (at < 0 || to < 0 || to >= members.Count)
                return;
            DropFlow(flowId, group, delta < 0 ? members[to] : to + 1 < members.Count ? members[to + 1] : null);
            return;
        }
        var units = TopLevelUnits(_session.Current);
        var unit = units.FindIndex(u => u.Group is null && u.FirstId == flowId);
        var target = unit + delta;
        if (unit < 0 || target < 0 || target >= units.Count)
            return;
        DropFlow(flowId, null, delta < 0 ? units[target].FirstId : target + 1 < units.Count ? units[target + 1].FirstId : null);
    }

    /// <summary>Alt+Left: a member leaves its group, to just after it.</summary>
    public void TakeOut(string flowId) => MoveFlowToGroup(flowId, null);

    /// <summary>The top-level items in order: a flow, or a group (named, with its first member's id).</summary>
    private static List<(string FirstId, string? Group)> TopLevelUnits(Configuration config)
    {
        var units = new List<(string, string?)>();
        foreach (var row in FlowListModel.Rows(config))
        {
            if (row is GroupRow { FlowIds: [var first, ..] } g)
                units.Add((first, g.Name));
            else if (row is FlowRow { Group: null } f)
                units.Add((f.FlowId, null));
        }
        return units;
    }

    private void SyncRows()
    {
        var byId = Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var units = TopLevelUnits(_session.Current);
        var desired = new List<object>();
        var live = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var filtering = IsFiltering; // a search or filter shows what it found, groups open
        var reorder = CanReorder;
        foreach (var row in FlowListModel.Rows(_session.Current, _sort, LastRunOf))
        {
            if (row is GroupRow g)
            {
                var shown = g.FlowIds.Select(id => byId[id]).Where(Matches).ToList();
                if (filtering && shown.Count == 0)
                    continue; // no header over nothing
                if (!_groups.TryGetValue(g.Name, out var header))
                    _groups[g.Name] = header = new GroupItemViewModel(this, g.Name);
                var at = units.FindIndex(u => u.Group is not null && StringComparer.CurrentCultureIgnoreCase.Equals(u.Group, g.Name));
                header.Update(g.Name, filtering ? shown : [.. g.FlowIds.Select(id => byId[id])], filtering || !_collapsed.Contains(g.Name), _session.CanEdit,
                    reorder && at > 0, reorder && at >= 0 && at < units.Count - 1, reorder);
                live.Add(g.Name);
                desired.Add(header);
            }
            else if (row is FlowRow f && (f.Group is null || filtering || !_collapsed.Contains(f.Group)) && Matches(byId[f.FlowId]))
            {
                desired.Add(byId[f.FlowId]);
            }
        }
        foreach (var gone in _groups.Keys.Where(k => !live.Contains(k)).ToList())
            _groups.Remove(gone);

        // In place, so the focused card and a switch being clicked keep their elements.
        var keep = desired.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
                Rows.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i]))
                continue;
            var from = Rows.IndexOf(desired[i]);
            if (from >= 0)
                Rows.Move(from, i);
            else
                Rows.Insert(i, desired[i]);
        }
        OnPropertyChanged(nameof(NoMatches));
    }

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

    /// <summary>
    /// Recomputes the relative times ("2 h ago"). Called when the page shows and the window activates; a run event does it
    /// through <see cref="Sync"/>. No timer ticks them (D-009).
    /// </summary>
    public void RefreshTimes()
    {
        var now = _time.GetUtcNow();
        foreach (var item in Items)
            item.UpdateTimes(now);
    }

    [RelayCommand]
    private void NewFlow()
    {
        _ = EndUndoAsync();
        var ids = _session.Current.Flows.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        Commit(c => c with { Flows = [.. c.Flows, new Flow(FlowIds.Unique("new-flow", ids), "New flow", [])] });
    }

    /// <summary>"Controller menu" (DESIGN §3.7): a large menu of the first four flows,. It has no trigger yet: give it a shortcut (D-086).</summary>
    public const string ControllerMenuName = "Controller menu";

    /// <summary>Builds the "Controller menu" flow for <paramref name="config"/>: items are the first four flows that exist.</summary>
    public static Flow ControllerMenu(Configuration config, string id)
    {
        ArgumentNullException.ThrowIfNull(config);
        var entries = new List<KeyValuePair<string, ParamValue>>();
        var items = config.Flows.Take(4).Select(f => (ParamValue)f.Id).ToArray();
        if (items.Length > 0)
            entries.Add(new("items", new ListParam([.. items])));
        entries.Add(new("size", (ParamValue)"large"));
        return new Flow(id, ControllerMenuName, [new StepNode("menu", ProviderCatalog.MenuShowId) { With = new(entries) }]);
    }

    [RelayCommand]
    private void NewControllerMenu()
    {
        _ = EndUndoAsync();
        var ids = _session.Current.Flows.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var flow = ControllerMenu(_session.Current, FlowIds.Unique("controller-menu", ids));
        Commit(c => c with { Flows = [.. c.Flows, flow] });
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
        Commit(c => c with { Flows = [.. c.Flows.Where(f => f.Id != flowId)] });
        _deleted = (flow, index);
        _deleteMark = _undo.Count; // the stack's top is this delete: the bar's Undo and Ctrl+Z are one step
        DeletedText = $"{flow.Name} was deleted.";
    }

    /// <summary>Undo: the flow back at its place, with its id.</summary>
    [RelayCommand]
    private void UndoDelete()
    {
        if (_deleted is not { } deleted)
            return;
        if (_undo.Count == _deleteMark)
        {
            Undo(); // clears the bar
            return;
        }
        _deleted = null;
        DeletedText = null;
        if (_session.Current.Flows.Any(f => f.Id == deleted.Flow.Id))
            return; // the file got a flow with that id meanwhile
        Commit(c =>
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
        ClearUndo();
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
        Commit(c => c with
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
        var now = _time.GetUtcNow();
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
            existing.Update(flow, _lastRuns.GetValueOrDefault(flow.Id), withErrors.GetValueOrDefault(flow.Id), _session.CanEdit, CanReorder, ReorderOffReason,
                FlowRefs.Callers(_session.Current, flow.Id).Count, now);
            existing.UpdateDelete(_session.CanEdit, DeleteOffReason(flow.Id));
            existing.UpdateRun(_engine.IsRunning, _liveRuns.ContainsValue(flow.Id));
        }
        IsEmpty = Items.Count == 0;
        SyncRows();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigSession.Current) && !_committing)
            ClearUndo(); // a hot reload or a flow saved elsewhere: the stack would restore stale text
        if (e.PropertyName is nameof(ConfigSession.Current) or nameof(ConfigSession.Problems))
            Sync();
        if (e.PropertyName == nameof(ConfigSession.CanEdit))
        {
            ClearUndo(); // the file became unreadable or readable again: the snapshots may be stale
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
        foreach (var group in _groups.Values)
            group.Refresh();
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

    /// <summary>The flow stands under a group header (indented, D-087).</summary>
    [ObservableProperty]
    public partial bool InGroup { get; private set; }

    /// <summary>The group the flow belongs to (its tray folder); null at the top level.</summary>
    public string? Group => _folder is { Length: > 0 } ? _folder : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenName), nameof(RunName))]
    public partial string Name { get; private set; } = "";

    /// <summary>The card's invoke name: it opens the flow on Enter/Space (D-070).</summary>
    public string OpenName => $"Open {Name}";

    /// <summary>The card's UIA name: "Connect Technics, Alt+Z, 5 steps, done 2 hours ago, in Audio, turned on".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Name, _hotkeys.Count == 0 ? "no shortcut" : FlowRowFacts.ShortcutTip(_hotkeys), FlowRowFacts.Steps(_steps).ToLowerInvariant(),
                IsRunning ? "running" : _lastRun is null ? "no recent run" : FlowRowFacts.LastRun(_lastRun, _now, spoken: true) };
            if (_folder is not null)
                parts.Add($"in {_folder}");
            parts.Add(IsEnabled ? "turned on" : "turned off");
            if (_errors > 0)
                parts.Add("needs fixing");
            if (NotInTray)
                parts.Add("not in the tray menu");
            if (HasDesktopShortcut)
                parts.Add("on your desktop");
            if (UsedBy.Length > 0)
                parts.Add(UsedBy.ToLowerInvariant());
            return string.Join(", ", parts);
        }
    }

    private IReadOnlyList<string> _hotkeys = [];
    private int _steps;
    private string? _folder;
    private RunRecord? _lastRun;
    private DateTimeOffset _now;

    /// <summary>The Run button's UIA name.</summary>
    public string RunName => $"{RunText} {Name}";

    /// <summary>"Stop" while this flow runs.</summary>
    public string RunText => IsRunning ? "Stop" : "Run";

    /// <summary>A run of this flow is going on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText), nameof(RunName), nameof(RunTip), nameof(Caption), nameof(Summary))]
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

    /// <summary>First shortcut, or "No shortcut".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    public partial string Shortcut { get; private set; } = "";

    /// <summary>All shortcuts, for the tooltip.</summary>
    [ObservableProperty]
    public partial string ShortcutTip { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    public partial string Steps { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    public partial string LastRun { get; private set; } = "";

    /// <summary>Line 2 of the card: "Alt+Z · 5 steps · Done 2 h ago"; "Running" while a run goes on.</summary>
    public string Caption => $"{Shortcut} · {Steps} · {(IsRunning ? "Running" : LastRun)}";

    /// <summary>The flow isn't in the tray menu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool NotInTray { get; private set; }

    /// <summary>"Used by 2 flows"; empty when no flow runs this one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsedBy), nameof(Summary))]
    public partial string UsedBy { get; private set; } = "";

    public bool HasUsedBy => UsedBy.Length > 0;

    [ObservableProperty]
    public partial string Problems { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasProblems { get; private set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DesktopMenuText), nameof(Summary))]
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
    [NotifyPropertyChangedFor(nameof(DragTip))]
    public partial bool CanEdit { get; private set; }

    /// <summary>Drag, Alt+arrows and Move to work: the file is editable and the list shows Your order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragTip))]
    public partial bool CanReorder { get; private set; }

    private string? _reorderOff;

    /// <summary>Name, shortcut, group and step names in one string for the search box.</summary>
    internal string SearchHaystack { get; private set; } = "";

    /// <summary>The grip's tooltip: what dragging does, or why it is off.</summary>
    public string DragTip => !CanEdit ? "This file is read-only, so the list can't be rearranged." : CanReorder ? "Drag to reorder" : _reorderOff ?? "Drag to reorder";

    internal void Update(Flow flow, RunRecord? lastRun, int errors, bool canEdit, bool canReorder, string? reorderOff, int callers, DateTimeOffset now)
    {
        _syncing = true;
        try
        {
            Name = flow.Name;
            Glyph = FlowGlyph.Decode(FlowGlyph.CodeOf(flow));
            CanEdit = canEdit;
            _reorderOff = reorderOff;
            CanReorder = canReorder;
            OnPropertyChanged(nameof(DragTip));
            _hotkeys = [.. flow.Hotkeys];
            _steps = flow.Nodes.OfType<StepNode>().Count();
            _folder = flow.Tray.Folder;
            InGroup = _folder is { Length: > 0 };
            _lastRun = lastRun;
            Shortcut = FlowRowFacts.Shortcut(_hotkeys);
            ShortcutTip = FlowRowFacts.ShortcutTip(_hotkeys);
            Steps = FlowRowFacts.Steps(_steps);
            NotInTray = !flow.Tray.Show;
            UsedBy = FlowRowFacts.UsedBy(callers);
            SearchHaystack = string.Join('\n', new[] { flow.Name, ShortcutTip, Shortcut, flow.Tray.Folder ?? "" }
                .Concat(flow.Nodes.OfType<StepNode>().Select(n => n.Step.Replace('.', ' ').Replace('_', ' ').Replace('-', ' '))));
            UpdateTimes(now);
            HasProblems = errors > 0;
            Problems = errors switch { 0 => "", 1 => "1 problem", _ => $"{errors} problems" };
            IsEnabled = flow.Enabled;
            if (_errors != errors)
            {
                _errors = errors;
                OnPropertyChanged(nameof(Summary));
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

    internal void UpdateTimes(DateTimeOffset now)
    {
        _now = now;
        LastRun = FlowRowFacts.LastRun(_lastRun, now);
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>YAML holds the code point in hex ("E7F4"); anything else falls back to the default.</summary>
    internal static string DecodeGlyph(string? code) => FlowGlyph.Decode(code);

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RunOffReason));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunTip));
        RunCommand.NotifyCanExecuteChanged();
        if (!_syncing)
            _page.SetEnabled(Id, value);
    }
}
