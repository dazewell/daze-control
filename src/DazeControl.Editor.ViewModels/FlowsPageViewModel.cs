using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels;

/// <summary>Flows page (UX §5.1): one card per flow in YAML order; the flow editor opens from it in M7.</summary>
public sealed partial class FlowsPageViewModel : ObservableObject, IDisposable
{
    private const int HistoryLimit = 100;

    private readonly ConfigSession _session;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private Dictionary<string, RunRecord> _lastRuns = [];

    public FlowsPageViewModel(ConfigSession session, IEngineClient engine, Action<Action> post)
    {
        _session = session;
        _engine = engine;
        _post = post;
        _session.PropertyChanged += OnSessionChanged;
        _engine.EventReceived += OnEngineEvent;
        _engine.StateChanged += OnEngineStateChanged;
        Sync();
    }

    public ObservableCollection<FlowItemViewModel> Items { get; } = [];

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
        var ids = _session.Current.Flows.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var id = "new-flow";
        for (var n = 2; ids.Contains(id); n++)
            id = string.Create(CultureInfo.InvariantCulture, $"new-flow-{n}");
        _session.Update(c => c with { Flows = [.. c.Flows, new Flow(id, "New flow", [])] });
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
                Items.Insert(i, existing);
            }
            else if (Items.IndexOf(existing) != i)
            {
                Items.Move(Items.IndexOf(existing), i);
            }
            existing.Update(flow, _lastRuns.GetValueOrDefault(flow.Id), withErrors.GetValueOrDefault(flow.Id), _session.CanEdit);
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
        if (e.Kind == EventKinds.RunFinished)
            _ = RefreshRunsAsync();
    }

    private void OnEngineStateChanged()
    {
        if (_engine.IsRunning)
            _ = RefreshRunsAsync();
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _engine.EventReceived -= OnEngineEvent;
        _engine.StateChanged -= OnEngineStateChanged;
    }
}

public sealed partial class FlowItemViewModel : ObservableObject
{
    /// <summary>Segoe Fluent "Lightning bolt" when the flow names no glyph.</summary>
    public const string DefaultGlyph = "";

    private readonly FlowsPageViewModel _page;
    private bool _syncing;

    internal FlowItemViewModel(FlowsPageViewModel page, string id)
    {
        _page = page;
        Id = id;
    }

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditName))]
    public partial string Name { get; private set; } = "";

    public string EditName => $"Edit {Name}";

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

    /// <summary>False while the file is read-only for the editor.</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    internal void Update(Flow flow, RunRecord? lastRun, int errors, bool canEdit)
    {
        _syncing = true;
        try
        {
            Name = flow.Name;
            Glyph = DecodeGlyph(flow.Glyph);
            CanEdit = canEdit;
            Shortcut = flow.Hotkeys.Count == 0 ? "No shortcut" : string.Join(", ", flow.Hotkeys);
            LastRun = lastRun is null ? "Not run yet" : $"{Runs.RunWords.Chip(lastRun.Outcome)} · {lastRun.Started.ToLocalTime():g}";
            HasProblems = errors > 0;
            Problems = errors switch { 0 => "", 1 => "1 problem", _ => $"{errors} problems" };
            IsEnabled = flow.Enabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>YAML holds the code point in hex ("E7F4"); anything else falls back to the default.</summary>
    internal static string DecodeGlyph(string? code) =>
        int.TryParse(code, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var value)
        && value is >= 0x20 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
            ? char.ConvertFromUtf32(value)
            : DefaultGlyph;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing)
            _page.SetEnabled(Id, value);
    }
}
