using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Runs;

/// <summary>One day of the Runs list ("Today", "Yesterday", "Friday, 30 October"), newest run first.</summary>
public sealed class RunDay(string title, IEnumerable<RunItemViewModel> runs) : List<RunItemViewModel>(runs)
{
    public string Title { get; } = title;

    public override string ToString() => Title;
}

/// <summary>A row of the Runs list (UX §5.5): glyph, name, trigger, time, result chip, duration.</summary>
public sealed class RunItemViewModel(RunRecord record, string glyph)
{
    public RunRecord Record { get; } = record;

    public string Glyph { get; } = glyph;

    public string Name => Record.FlowName;

    public string Trigger => Record.Trigger;

    public string Time => Record.Started.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public string Result => RunWords.Chip(Record.Outcome);

    public FlowOutcome Outcome => Record.Outcome;

    public string Duration => RunWords.Seconds(Record.Duration);

    /// <summary>UIA reads list rows by <c>ToString()</c>.</summary>
    public override string ToString() => $"{Name}, {Result}, {Trigger}, {Time}, {Duration}";
}

/// <summary>Runs page (UX §5.5): the engine's last 100 runs grouped by day, and the selected one's detail.</summary>
public sealed partial class RunsPageViewModel : ObservableObject, IDisposable
{
    private readonly ConfigSession _session;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;

    public RunsPageViewModel(ConfigSession session, IEngineClient engine, Action<Action> post, TimeProvider? time = null)
    {
        _session = session;
        _engine = engine;
        _post = post;
        _time = time ?? TimeProvider.System;
        _engine.EventReceived += OnEngineEvent;
    }

    public ObservableCollection<RunDay> Days { get; } = [];

    /// <summary>No runs yet (or no engine to ask): the page shows its empty state.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    [ObservableProperty]
    public partial RunDetailViewModel? Detail { get; private set; }

    /// <summary>Asks the engine for its history (the page opened, a run finished).</summary>
    public async Task RefreshAsync()
    {
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.History, Limit = RunHistoryLimit }).ConfigureAwait(false);
        if (answer?.History is { } history)
            _post(() => Show(history));
    }

    /// <summary>The engine keeps the last 100 runs (FEATURES F5).</summary>
    public const int RunHistoryLimit = 100;

    internal void Show(IEnumerable<RunRecord> history)
    {
        var today = _time.GetLocalNow().Date;
        Days.Clear();
        foreach (var day in history.OrderByDescending(r => r.Started).GroupBy(r => r.Started.ToLocalTime().Date))
            Days.Add(new RunDay(DayTitle(day.Key, today), day.Select(r => new RunItemViewModel(r, GlyphOf(r.FlowId)))));
        IsEmpty = Days.Count == 0;
        if (Detail is not { } shown)
            return;
        if (Days.SelectMany(d => d).FirstOrDefault(r => r.Record.RunId == shown.Record.RunId) is not { } item)
        {
            Detail = null;
            return;
        }
        // The flow may have changed or gone since the detail was made (brackets, names, "Open in editor").
        var flow = FlowOf(item.Record);
        if (ReferenceEquals(flow, shown.Flow))
            return;
        Select(item);
        if (shown.SelectedBar is { } bar)
            Detail!.SelectedBar = Detail.Bars.FirstOrDefault(b => b.NodeId == bar.NodeId);
    }

    public void Select(RunItemViewModel? item) =>
        Detail = item is null ? null : new RunDetailViewModel(item.Record, FlowOf(item.Record));

    private Flow? FlowOf(RunRecord record) => _session.Current.Flows.FirstOrDefault(f => f.Id == record.FlowId);

    internal static string DayTitle(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString(day.Year == today.Year ? "dddd, d MMMM" : "d MMMM yyyy", CultureInfo.CurrentCulture);

    private string GlyphOf(string flowId)
    {
        var flow = _session.Current.Flows.FirstOrDefault(f => f.Id == flowId);
        var code = flow?.Glyph ?? flow?.Nodes.OfType<StepNode>().Select(s => Core.Catalog.ProviderCatalog.Find(s.Step)?.Glyph).OfType<string>().FirstOrDefault();
        return FlowItemViewModel.DecodeGlyph(code ?? "E8FD");
    }

    private void OnEngineEvent(PipeEvent e)
    {
        if (e.Kind == EventKinds.RunFinished)
            _ = RefreshSoonAsync();
    }

    /// <summary>The engine records a run just after its runFinished event.</summary>
    private async Task RefreshSoonAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), _time).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public void Dispose() => _engine.EventReceived -= OnEngineEvent;
}
