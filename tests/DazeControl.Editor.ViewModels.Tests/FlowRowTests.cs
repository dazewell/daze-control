using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class FlowRowTests : IDisposable
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Noon = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _manual = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly Clock _clock = new(Noon);
    private readonly ConfigSession _session;

    public FlowRowTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, ConfigSessionTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _manual), _manual, a => a());
        _session.Load();
    }

    public void Dispose()
    {
        _session.Dispose();
        _manual.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static RunRecord Run(string flow, TimeSpan ago, FlowOutcome outcome = FlowOutcome.Succeeded) =>
        new(Guid.NewGuid(), flow, flow, "Editor", Noon - ago, TimeSpan.FromSeconds(1), outcome, []);

    private FlowsPageViewModel Page() => new(_session, _engine, a => a(), time: _clock);

    private async Task<FlowsPageViewModel> PageWithRunsAsync(params RunRecord[] runs)
    {
        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = true, History = runs };
        var page = Page();
        await page.RefreshRunsAsync();
        return page;
    }

    [Fact]
    public void ShortcutShowsTheFirstWithAPlusForTheRestAndNoShortcutWithoutOne()
    {
        Assert.Equal("No shortcut", FlowRowFacts.Shortcut([]));
        Assert.Equal("Alt+Z", FlowRowFacts.Shortcut(["Alt+Z"]));
        Assert.Equal("Alt+Z +1", FlowRowFacts.Shortcut(["Alt+Z", "F5"]));
        Assert.Equal("Alt+Z, F5", FlowRowFacts.ShortcutTip(["Alt+Z", "F5"]));
    }

    [Fact]
    public void StepsAndUsedByAreWorded()
    {
        Assert.Equal("No steps", FlowRowFacts.Steps(0));
        Assert.Equal("1 step", FlowRowFacts.Steps(1));
        Assert.Equal("5 steps", FlowRowFacts.Steps(5));
        Assert.Equal("", FlowRowFacts.UsedBy(0));
        Assert.Equal("Used by 1 flow", FlowRowFacts.UsedBy(1));
        Assert.Equal("Used by 2 flows", FlowRowFacts.UsedBy(2));
    }

    [Fact]
    public void LastRunReadsAsAWordAndARelativeTimeThenAWeekdayAfterADay()
    {
        Assert.Equal("No recent run", FlowRowFacts.LastRun(null, Noon));
        Assert.Equal("Done just now", FlowRowFacts.LastRun(Run("a", TimeSpan.FromSeconds(20)), Noon));
        Assert.Equal("Done 5 min ago", FlowRowFacts.LastRun(Run("a", TimeSpan.FromMinutes(5)), Noon));
        Assert.Equal("Didn't finish 2 h ago", FlowRowFacts.LastRun(Run("a", TimeSpan.FromHours(2), FlowOutcome.Failed), Noon));
        Assert.Equal("done 2 hours ago", FlowRowFacts.LastRun(Run("a", TimeSpan.FromHours(2)), Noon, spoken: true));
        Assert.Equal("done 1 hour ago", FlowRowFacts.LastRun(Run("a", TimeSpan.FromMinutes(61)), Noon, spoken: true));
        var old = Run("a", TimeSpan.FromDays(2));
        Assert.Equal("Stopped " + old.Started.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            FlowRowFacts.LastRun(old with { Outcome = FlowOutcome.Cancelled }, Noon));
    }

    [Fact]
    public async Task TheRowCarriesTheShortcutStepCountAndLastRun()
    {
        using var page = await PageWithRunsAsync(Run("connect-technics", TimeSpan.FromHours(2)));
        var row = page.Items[0];

        Assert.Equal("Alt+Z · 1 step · Done 2 h ago", row.Caption);
        Assert.Equal("Connect Technics, Alt+Z, 1 step, done 2 hours ago, turned on", row.Summary);
        Assert.Equal("No shortcut · 1 step · No recent run", page.Items[1].Caption);
    }

    [Fact]
    public async Task ARunningFlowSaysRunningAndAFlowWithoutRecordsSaysNoRecentRun()
    {
        using var page = await PageWithRunsAsync();
        Assert.Equal("No recent run", page.Items[0].LastRun);

        _engine.Raise(new PipeEvent { Kind = EventKinds.RunStarted, RunId = Guid.NewGuid(), FlowId = "tv" });

        Assert.EndsWith("· Running", page.Items[1].Caption);
        Assert.Contains("running", page.Items[1].Summary);
    }

    [Fact]
    public async Task TimesAreRecomputedOnlyWhenAskedNotByTheClock()
    {
        using var page = await PageWithRunsAsync(Run("tv", TimeSpan.FromMinutes(5)));
        Assert.Equal("Done 5 min ago", page.Items[1].LastRun);

        _clock.Now = Noon.AddHours(3);
        Assert.Equal("Done 5 min ago", page.Items[1].LastRun); // nothing ticks it (D-009)

        page.RefreshTimes();
        Assert.Equal("Done 3 h ago", page.Items[1].LastRun);
    }

    [Fact]
    public async Task ARunEventRefreshesTheTimesThroughTheHistory()
    {
        var runs = new List<RunRecord>();
        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = true, History = [.. runs] };
        using var page = Page();
        await page.RefreshRunsAsync();
        Assert.Equal("No recent run", page.Items[1].LastRun);

        runs.Add(Run("tv", TimeSpan.FromSeconds(1)));
        _engine.Raise(new PipeEvent { Kind = EventKinds.RunFinished, RunId = Guid.NewGuid(), FlowId = "tv" });
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal("Done just now", page.Items[1].LastRun);
    }

    [Fact]
    public async Task TheHistoryRequestAsksForTheWholeFile()
    {
        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = true, History = [] };
        using var page = Page();
        await page.RefreshRunsAsync();

        Assert.Equal(200, Assert.Single(_engine.Requests, r => r.Op == Ops.History).Limit);
    }

    [Fact]
    public void NotInTrayUsedByAndFolderShowOnTheRowAndInItsName()
    {
        _session.Update(c => c with
        {
            Flows =
            [
                .. c.Flows.Select(f => f.Id == "tv" ? f with { Tray = new TraySettings { Show = false, Folder = "Audio" }, Enabled = false } : f),
                new Flow("boss", "Boss", [new StepNode("go", "flow.run") { With = new([new("flow", (ParamValue)"tv")]) }]),
            ],
        });
        using var page = Page();
        var tv = page.Items.Single(i => i.Id == "tv");

        Assert.True(tv.NotInTray);
        Assert.True(tv.HasUsedBy);
        Assert.Equal("Used by 1 flow", tv.UsedBy);
        Assert.False(page.Items.Single(i => i.Id == "boss").HasUsedBy);
        Assert.Equal("TV, no shortcut, 1 step, no recent run, in Audio, turned off, not in the tray menu, used by 1 flow", tv.Summary);
    }

    [Fact]
    public void ABlankFlowHasNoSteps()
    {
        _session.Update(c => c with { Flows = [.. c.Flows, new Flow("blank", "Blank", [])] });
        using var page = Page();

        Assert.Equal("No shortcut · No steps · No recent run", page.Items.Single(i => i.Id == "blank").Caption);
    }
}
