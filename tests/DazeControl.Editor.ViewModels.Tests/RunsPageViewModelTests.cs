using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Editor.ViewModels.Runs;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class RunsPageViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FakeEngine _engine = new() { IsRunning = true };

    public RunsPageViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
    }

    public void Dispose()
    {
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static NodeResult Ran(string id, double start, double end, NodeOutcome outcome = NodeOutcome.Succeeded, int order = 1,
        string? error = null, params double[] retries) =>
        new(id, outcome, 1 + retries.Length, error, TimeSpan.FromSeconds(end - start), TimeSpan.FromSeconds(start), order,
            retries.Length == 0 ? null : [.. retries.Select(TimeSpan.FromSeconds)]);

    private static RunRecord Run(DateTimeOffset started, FlowOutcome outcome = FlowOutcome.Succeeded, double seconds = 3, params NodeResult[] nodes) =>
        new(Guid.NewGuid(), "f", "Connect Technics", "Alt+Z", started, TimeSpan.FromSeconds(seconds), outcome, nodes);

    [Fact]
    public async Task RunsAreGroupedByDayNewestFirst()
    {
        var today = _time.GetLocalNow();
        var now = new DateTimeOffset(today.Date.AddHours(12), today.Offset); // noon: an hour earlier is still today
        var history = new[] { Run(now.AddHours(-1)), Run(now.AddDays(-1)), Run(now.AddMinutes(-5)), Run(now.AddDays(-9)) };
        _engine.Answer = r => new PipeMessage { Ok = true, History = r.Op == Ops.History && r.Limit == 100 ? history : null };
        using var page = new RunsPageViewModel(_session, _engine, action => action(), _time);

        await page.RefreshAsync();

        Assert.False(page.IsEmpty);
        Assert.Equal(3, page.Days.Count);
        Assert.Equal(["Today", "Yesterday"], page.Days.Take(2).Select(d => d.Title));
        Assert.Equal([history[2], history[0]], page.Days[0].Select(r => r.Record));
        var row = page.Days[0][0];
        Assert.Equal(("Done", "Alt+Z", "3 s"), (row.Result, row.Trigger, row.Duration));
    }

    [Fact]
    public async Task NoEngineMeansTheEmptyState()
    {
        using var page = new RunsPageViewModel(_session, new FakeEngine(), action => action(), _time);

        await page.RefreshAsync();

        Assert.True(page.IsEmpty);
    }

    [Fact]
    public void TheDetailSaysWhatHappenedAndPacksParallelStepsIntoLanes()
    {
        var run = Run(_time.GetLocalNow(), FlowOutcome.PartiallySucceeded, 4,
            Ran("bt", 0, 2, order: 1),
            Ran("out", 2, 2.5, NodeOutcome.FailedIgnored, order: 2, error: "No such device"),
            Ran("vol", 2, 4, order: 3, retries: [3]),
            new NodeResult("note", NodeOutcome.Skipped, Attempts: 0));
        var detail = new RunDetailViewModel(run, _session.Current.Flows[0]);

        detail.Layout(400);

        Assert.Equal("Done, with a problem. Set default device didn't work, so it went on without it.", detail.Sentence);
        Assert.True(detail.HasTimeline);
        Assert.Equal(2, detail.Timeline.Lanes);
        var bars = detail.Bars.ToDictionary(b => b.NodeId);
        Assert.Equal((0, 200), (bars["bt"].X, bars["bt"].Width)); // 100 DIP a second
        Assert.Equal(bars["bt"].Y, bars["out"].Y);
        Assert.NotEqual(bars["out"].Y, bars["vol"].Y);
        Assert.True(bars["out"].IsMayFailFailed);
        Assert.Equal([100.0], bars["vol"].Notches);
        Assert.Equal(["Show a message: skipped"], detail.NotRun);
        Assert.Equal(RunDetailViewModel.AxisHeight + 2 * RunDetailViewModel.LaneHeight, detail.Height);
    }

    [Fact]
    public void AShortStepStillGetsAVisibleBar()
    {
        var run = Run(_time.GetLocalNow(), seconds: 100, nodes: [Ran("bt", 0, 0.001), Ran("vol", 99.999, 100, order: 2)]);
        var detail = new RunDetailViewModel(run, null);

        detail.Layout(500);

        Assert.All(detail.Bars, b => Assert.Equal(RunDetailViewModel.MinBarWidth, b.Width));
        Assert.All(detail.Bars, b => Assert.True(b.X + b.Width <= 500));
    }

    [Fact]
    public void InstantStepsInALaneDontHideEachOther()
    {
        var run = Run(_time.GetLocalNow(), seconds: 8, nodes: [Ran("a", 0, 2), Ran("b", 2, 2.001, order: 2), Ran("c", 2.001, 2.002, order: 3)]);
        var detail = new RunDetailViewModel(run, null);

        detail.Layout(800);

        Assert.Equal(1, detail.Timeline.Lanes);
        var bars = detail.Bars.OrderBy(b => b.X).ToList();
        Assert.Equal(["a", "b", "c"], bars.Select(b => b.NodeId));
        Assert.All(bars.Zip(bars.Skip(1)), p => Assert.True(p.Second.X >= p.First.X + p.First.Width));
    }

    [Fact]
    public void InstantStepsAtTheRightEdgeKeepTheirGap()
    {
        var run = Run(_time.GetLocalNow(), seconds: 2.002, nodes: [Ran("a", 0, 2), Ran("b", 2, 2.001, order: 2), Ran("c", 2.001, 2.002, order: 3)]);
        var detail = new RunDetailViewModel(run, null);

        detail.Layout(800);

        var bars = detail.Bars.OrderBy(b => b.X).ToList();
        Assert.Equal(["a", "b", "c"], bars.Select(b => b.NodeId));
        Assert.All(bars.Zip(bars.Skip(1)), p => Assert.True(p.Second.X >= p.First.X + p.First.Width + RunDetailViewModel.BarGap));
        Assert.Equal(800, bars[^1].X + bars[^1].Width, 3);
    }

    [Fact]
    public void ABracketHugsItsMembersAfterTheyAreFittedAtTheRightEdge()
    {
        var flow = _session.Current.Flows[0];
        var run = Run(_time.GetLocalNow(), seconds: 2.002, nodes:
            [Ran("bt", 0, 2), Ran("w1", 2, 2.001, order: 2), Ran("w2", 2, 2.001, order: 3), Ran("out", 2.001, 2.002, order: 4)]);
        var detail = new RunDetailViewModel(run, flow);

        detail.Layout(800);

        var bracket = Assert.Single(detail.Brackets);
        var members = detail.Bars.Where(b => b.NodeId is "w1" or "w2").ToList();
        Assert.All(members, m => Assert.InRange(m.X, bracket.X, bracket.X + bracket.Width - m.Width));
    }

    [Fact]
    public void ABadCardInTheCurrentFlowDrawsNoBrackets()
    {
        var flow = _session.Current.Flows[0];
        flow = flow with { Nodes = new(flow.Nodes.Append(new GroupNode("again", new(["w1"])))) }; // w1 in two cards
        var run = Run(_time.GetLocalNow(), seconds: 2, nodes: [Ran("w1", 0, 1), Ran("w2", 0, 2, order: 2)]);

        var detail = new RunDetailViewModel(run, flow);

        Assert.Empty(detail.Brackets);
        Assert.Equal(2, detail.Bars.Count);
    }

    [Fact]
    public void ACardThatRanIsNotListedAsSkippedWhenItsFlowIsGone()
    {
        var run = Run(_time.GetLocalNow(), seconds: 2, nodes:
            [Ran("w1", 0, 1), new NodeResult("both", NodeOutcome.Succeeded, Attempts: 0), new NodeResult("x", NodeOutcome.Skipped, Attempts: 0)]);

        var detail = new RunDetailViewModel(run, null);

        Assert.Equal(["x: skipped"], detail.NotRun);
    }

    [Fact]
    public void ARefreshRebuildsTheDetailWhenItsFlowWasDeleted()
    {
        var history = new[] { Run(_time.GetLocalNow(), nodes: [Ran("bt", 0, 1)]) };
        using var page = new RunsPageViewModel(_session, _engine, action => action(), _time);
        page.Show(history);
        page.Select(page.Days[0][0]);
        page.Detail!.SelectedBar = page.Detail.Bars[0];
        Assert.True(page.Detail.CanOpenInEditor);

        File.WriteAllText(_session.Path, "version: 1\nflows: {}\n");
        _session.Load();
        page.Show(history);

        Assert.False(page.Detail!.CanOpenInEditor);
        Assert.Equal("bt", page.Detail.SelectedBar?.NodeId);
    }

    [Fact]
    public void ACardIsABracketOverItsMembersLanes()
    {
        var run = Run(_time.GetLocalNow(), seconds: 2, nodes:
            [Ran("w1", 0, 1, order: 1), Ran("w2", 0, 2, order: 2), new NodeResult("both", NodeOutcome.Succeeded, Attempts: 0)]);
        var detail = new RunDetailViewModel(run, _session.Current.Flows[0]);

        var bracket = Assert.Single(detail.Brackets);

        Assert.Equal(2 * RunDetailViewModel.LaneHeight - 4, bracket.Height);
        Assert.Empty(detail.NotRun); // the card itself never "runs"
    }

    [Fact]
    public void PickingABarShowsItsLines()
    {
        var run = Run(_time.GetLocalNow(), FlowOutcome.Failed, 10,
            Ran("bt", 0, 10, NodeOutcome.Failed, error: "Bluetooth didn't connect in 10 s.\nHRESULT 0x80070490", retries: [5]));
        var detail = new RunDetailViewModel(run, _session.Current.Flows[0]);

        detail.SelectedBar = detail.Bars[0];

        Assert.Equal(["Connect: didn't work", "Started at 0 s, 2 tries, took 10 s.", "Bluetooth didn't connect in 10 s.", "HRESULT 0x80070490"],
            detail.SelectedLines);
        Assert.Equal("Connect Technics didn't finish. Bluetooth didn't connect in 10 s.", detail.Sentence);
    }

    [Fact]
    public void RunsFromBeforeTheTimelineSayIt()
    {
        var detail = new RunDetailViewModel(Run(_time.GetLocalNow(), nodes: [new NodeResult("bt", NodeOutcome.Succeeded)]), null);

        Assert.False(detail.HasTimeline);
    }

    [Fact]
    public void TicksAreRoundSteps()
    {
        Assert.Equal([0, 1, 2, 3], RunDetailViewModel.TickTimes(TimeSpan.FromSeconds(3.1)).Select(t => t.TotalSeconds));
        Assert.Equal([0, 0.5, 1, 1.5, 2, 2.5], RunDetailViewModel.TickTimes(TimeSpan.FromSeconds(2.5)).Select(t => t.TotalSeconds));
    }

    [Fact]
    public void OpenInEditorShowsTheRunOnTheCanvas()
    {
        using var shell = new ShellViewModel(_session, _engine, action => action(), _folder);
        shell.Page = EditorPage.Runs;
        var run = Run(_time.GetLocalNow(), FlowOutcome.Failed, 1, Ran("bt", 0, 1, NodeOutcome.Failed, error: "No"));

        shell.OpenRun(run);

        Assert.Equal(EditorPage.Flows, shell.Page);
        Assert.Equal(Canvas.NodeRunState.Failed, shell.Editor!.Find("bt")!.RunState);
        Assert.True(shell.Run!.IsInfoOpen);
    }

    [Fact]
    public void DayTitlesNameRecentDays()
    {
        var today = new DateTime(2026, 11, 2);

        Assert.Equal("Today", RunsPageViewModel.DayTitle(today, today));
        Assert.Equal("Yesterday", RunsPageViewModel.DayTitle(today.AddDays(-1), today));
        Assert.Contains("2025", RunsPageViewModel.DayTitle(new DateTime(2025, 12, 30), today), StringComparison.Ordinal);
    }
}
