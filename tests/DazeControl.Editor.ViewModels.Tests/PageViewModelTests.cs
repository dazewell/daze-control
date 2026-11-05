using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

internal sealed class FakeEngine : IEngineClient
{
    public bool IsRunning { get; set; }

    public Func<PipeRequest, PipeMessage>? Answer { get; set; }

    public List<PipeRequest> Requests { get; } = [];

    public event Action? StateChanged;

    public event Action<PipeEvent>? EventReceived;

    public Task<PipeMessage?> SendAsync(PipeRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(IsRunning && Answer is not null ? Answer(request) : null);
    }

    public void SetRunning(bool running)
    {
        IsRunning = running;
        StateChanged?.Invoke();
    }

    public void Raise(PipeEvent e) => EventReceived?.Invoke(e);
}

public sealed class PageViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new();
    private readonly ConfigSession _session;

    public PageViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(ConfigPath, ConfigSessionTests.Yaml);
        _session = new ConfigSession(new ConfigStore(ConfigPath, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
    }

    private string ConfigPath => Path.Combine(_folder, "config.yaml");

    public void Dispose()
    {
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void FlowCardsFollowTheFileInOrder()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        Assert.Equal(["connect-technics", "tv"], page.Items.Select(i => i.Id));
        var technics = page.Items[0];
        Assert.Equal("Connect Technics", technics.Name);
        Assert.Equal("Alt+Z", technics.Shortcut);
        Assert.Equal("No shortcut", page.Items[1].Shortcut);
        Assert.Equal(FlowGlyph.Decode(FlowGlyph.CodeOf(_session.Current.Flows[0])), technics.Glyph);
        Assert.True(technics.IsEnabled);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public void ACardShowsTheFlowsOwnGlyphElseItsFirstStepsAsFlowSettingsDo()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        var stepGlyph = FlowGlyph.Decode(FlowGlyph.FirstStepCode(_session.Current.Flows[1]));

        Assert.NotEqual(FlowGlyph.Default, stepGlyph);
        Assert.Equal(stepGlyph, page.Items[1].Glyph);

        _session.Update(c => c with { Flows = [.. c.Flows.Select(f => f.Id == "tv" ? f with { Glyph = "E7F4" } : f)] });
        Assert.Equal("", page.Items[1].Glyph);

        _session.Update(c => c with { Flows = [.. c.Flows.Select(f => f.Id == "tv" ? f with { Glyph = null, Nodes = [] } : f)] });
        Assert.Equal("", page.Items[1].Glyph);
    }

    [Fact]
    public void RunIsOffWithAReasonUntilTheEngineRunsAndTheFlowIsOnAndValid()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        var tv = page.Items[1];

        Assert.False(tv.CanRun);
        Assert.Equal("Start Daze Control to run flows.", tv.RunOffReason);

        _engine.SetRunning(true);
        Assert.True(tv.CanRun);
        Assert.Null(tv.RunOffReason);
        Assert.True(tv.RunCommand.CanExecute(null));

        tv.IsEnabled = false;
        Assert.False(tv.CanRun);
        Assert.Equal("Turn it on to run it.", tv.RunOffReason);
        Assert.False(tv.RunCommand.CanExecute(null));

        tv.IsEnabled = true;
        _session.Update(c => c with { Flows = [.. c.Flows.Select(f => f.Id == "tv" ? f with { Hotkeys = ["Nope+Q"] } : f)] });
        Assert.False(tv.CanRun);
        Assert.Equal("Fix its problems to run it.", tv.RunOffReason);
        Assert.True(page.Items[0].CanRun);
    }

    [Fact]
    public void RunStartsTheSavedFlowFromTheEditorAndReadsStopWhileItRuns()
    {
        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = true };
        _engine.SetRunning(true);
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        var tv = page.Items[1];
        Assert.Equal("Run", tv.RunText);
        Assert.Equal("Run TV", tv.RunName);
        Assert.Equal("Open TV", tv.OpenName);

        tv.RunCommand.Execute(null);

        var run = Assert.Single(_engine.Requests, r => r.Op == Ops.Run);
        Assert.Equal("tv", run.Flow);
        Assert.Equal("Editor", run.Trigger);
        Assert.False(run.DryRun);
        Assert.Null(run.Config); // the saved version, not a draft

        var runId = Guid.NewGuid();
        _engine.Raise(new PipeEvent { Kind = EventKinds.RunStarted, FlowId = "tv", RunId = runId });
        Assert.True(tv.IsRunning);
        Assert.Equal("Stop", tv.RunText);
        Assert.Equal("Stop TV", tv.RunName);
        Assert.False(page.Items[0].IsRunning);

        tv.RunCommand.Execute(null);
        var cancel = Assert.Single(_engine.Requests, r => r.Op == Ops.Cancel);
        Assert.Equal("tv", cancel.Flow);

        _engine.Raise(new PipeEvent { Kind = EventKinds.RunFinished, FlowId = "tv", RunId = runId, Outcome = FlowOutcome.Cancelled });
        Assert.False(tv.IsRunning);
        Assert.Equal("Run", tv.RunText);
    }

    [Fact]
    public void StopStaysAvailableForARunningFlowThatWasTurnedOff()
    {
        _engine.SetRunning(true);
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        var tv = page.Items[1];

        _engine.Raise(new PipeEvent { Kind = EventKinds.NodeStarted, FlowId = "tv", RunId = Guid.NewGuid(), NodeId = "wait" });
        tv.IsEnabled = false;

        Assert.True(tv.IsRunning);
        Assert.True(tv.RunCommand.CanExecute(null));

        _engine.SetRunning(false);
        Assert.False(tv.IsRunning);
        Assert.False(tv.RunCommand.CanExecute(null));
        Assert.Equal("Start Daze Control to run flows.", tv.RunOffReason);
    }

    [Fact]
    public void AnEngineThatWentAwayClearsLiveRunsEvenIfItIsBackBeforeTheUiCatchesUp()
    {
        var posted = new List<Action>();
        _engine.SetRunning(true);
        using var page = new FlowsPageViewModel(_session, _engine, posted.Add);
        posted.ForEach(a => a());
        posted.Clear();
        _engine.Raise(new PipeEvent { Kind = EventKinds.RunStarted, FlowId = "tv", RunId = Guid.NewGuid() });
        posted.ForEach(a => a());
        posted.Clear();
        Assert.True(page.Items[1].IsRunning);

        _engine.SetRunning(false);
        _engine.SetRunning(true);
        posted.ForEach(a => a());

        Assert.False(page.Items[1].IsRunning);
        Assert.True(page.Items[1].CanRun);
    }

    [Fact]
    public void ARefusedRunSaysWhy()
    {
        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = r.Op != Ops.Run, Error = "TV is already running." };
        _engine.SetRunning(true);
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        page.Items[1].RunCommand.Execute(null);

        Assert.Equal("TV is already running.", page.RunError);
        Assert.True(page.HasRunError);

        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = true };
        page.Items[1].RunCommand.Execute(null);
        Assert.False(page.HasRunError);
    }

    [Fact]
    public void TheOnToggleTurnsTheFlowOffInTheFileAndKeepsTheCard()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        var card = page.Items[1];

        card.IsEnabled = false;
        _session.Flush();

        Assert.Same(card, page.Items[1]);
        Assert.False(_session.Current.Flows[1].Enabled);
        Assert.Contains("enabled: false", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void NewFlowGetsAFreeId()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        page.NewFlowCommand.Execute(null);
        page.NewFlowCommand.Execute(null);

        Assert.Equal(["connect-technics", "tv", "new-flow", "new-flow-2"], page.Items.Select(i => i.Id));
    }

    [Fact]
    public void ACardShowsTheFlowsNewestRunAndRefreshesWhenARunFinishes()
    {
        var started = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        RunRecord Run(FlowOutcome outcome, int minutes) =>
            new(Guid.NewGuid(), "tv", "TV", "Hotkey", started.AddMinutes(minutes), TimeSpan.FromSeconds(1), outcome, []);
        var history = new[] { Run(FlowOutcome.Failed, 0) };
        _engine.IsRunning = true;
        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = true, History = history };
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        _engine.Raise(new PipeEvent { Kind = EventKinds.RunFinished, FlowId = "tv" });
        Assert.StartsWith("Didn't finish", page.Items[1].LastRun, StringComparison.Ordinal);

        history = [Run(FlowOutcome.Failed, 0), Run(FlowOutcome.Succeeded, 5)];
        _engine.Raise(new PipeEvent { Kind = EventKinds.RunFinished, FlowId = "tv" });

        Assert.StartsWith("Done ·", page.Items[1].LastRun, StringComparison.Ordinal);
        Assert.Equal("Not run yet", page.Items[0].LastRun);
    }

    [Fact]
    public void AFlowWithErrorsSaysHowMany()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        _session.Update(c => c with { Flows = [.. c.Flows.Select(f => f.Id == "tv" ? f with { Hotkeys = ["Nope+Q"] } : f)] });

        Assert.True(page.Items[1].HasProblems);
        Assert.Equal("1 problem", page.Items[1].Problems);
        Assert.False(page.Items[0].HasProblems);
    }

    [Fact]
    public void PopUpSettingsWriteTheFileAndSecondsStayInRange()
    {
        using var page = new SettingsPageViewModel(_session, _engine, a => a(), "logs");

        page.PopupPositionIndex = (int)PopupPosition.TopRight;
        page.PopupSeconds = 99;

        Assert.Equal(new PopupSettings(PopupPosition.TopRight, SettingsPageViewModel.MaxPopupSeconds), _session.Current.Settings.Popup);
    }

    [Fact]
    public void StepDefaultsAddEditAndRemoveAnEntry()
    {
        using var page = new SettingsPageViewModel(_session, _engine, a => a(), "logs");
        var defaults = page.StepDefaults;
        Assert.True(defaults.IsEmpty);
        Assert.Contains(defaults.Addable, i => i.Step == "wait");

        defaults.Add("wait");
        var row = Assert.Single(defaults.Rows);
        Assert.Equal("wait", row.StepId);
        Assert.DoesNotContain(defaults.Addable, i => i.Step == "wait");
        Assert.NotNull(_session.Current.Settings.StepDefaults["wait"].Timeout);

        row.TimeoutSeconds = 2;
        row.RetryCount = 2;
        row.PolicyIndex = (int)FailurePolicy.Skip;
        var entry = _session.Current.Settings.StepDefaults["wait"];
        Assert.Equal(new StepDefault { Timeout = TimeSpan.FromSeconds(2), RetryCount = 2, OnFailure = FailurePolicy.Skip }, entry);
        Assert.Equal("Give up after 2 s · Try again 2 times · Skip what depends on it", row.Summary);

        row.ResetTimeout();
        row.ResetRetryCount();
        Assert.Equal(new StepDefault { OnFailure = FailurePolicy.Skip }, _session.Current.Settings.StepDefaults["wait"]);

        row.ResetPolicy(); // nothing left: the entry goes
        Assert.Empty(_session.Current.Settings.StepDefaults);
        Assert.True(defaults.IsEmpty);

        defaults.Add("wait");
        defaults.Remove("wait");
        Assert.True(defaults.IsEmpty);
    }

    [Fact]
    public void StepDefaultsCountTheStepsAndFlowsThatUseAType()
    {
        using var page = new SettingsPageViewModel(_session, _engine, a => a(), "logs");

        Assert.Equal((2, 2), page.StepDefaults.Usage("wait"));
        Assert.Equal((0, 0), page.StepDefaults.Usage("audio.setDefault"));
        page.StepDefaults.Add("wait");
        Assert.Equal("Used by 2 steps in 2 flows", page.StepDefaults.Rows[0].UsedBy);
        page.StepDefaults.Add("audio.setDefault");
        Assert.Equal("Not used yet", page.StepDefaults.Rows.Single(r => r.StepId == "audio.setDefault").UsedBy);
    }

    [Fact]
    public void StepDefaultsAutosaveWritesOnlyTheirKey()
    {
        using var page = new SettingsPageViewModel(_session, _engine, a => a(), "logs");
        page.StepDefaults.Add("wait"); // the first save normalises the file's layout: take the baseline after it
        page.StepDefaults.Remove("wait");
        _session.Flush();
        var before = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("stepDefaults", before, StringComparison.Ordinal);

        page.StepDefaults.Add("wait");
        page.StepDefaults.Rows[0].RetryCount = 3;
        _session.Flush();

        var after = File.ReadAllText(ConfigPath);
        Assert.Contains("stepDefaults:", after, StringComparison.Ordinal);
        Assert.Contains("wait:", after, StringComparison.Ordinal);
        page.StepDefaults.Remove("wait");
        _session.Flush();
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void StepDefaultsKeepValuesInRangeAndFollowOutsideChanges()
    {
        using var page = new SettingsPageViewModel(_session, _engine, a => a(), "logs");
        page.StepDefaults.Add("wait");
        var row = page.StepDefaults.Rows[0];

        row.RetryCount = 99;
        row.TimeoutSeconds = 0;
        Assert.Equal(StepDefaultsEditor.MaxRetryCount, _session.Current.Settings.StepDefaults["wait"].RetryCount);
        Assert.Equal(TimeSpan.FromSeconds(0.1), _session.Current.Settings.StepDefaults["wait"].Timeout);

        _session.Update(c => c with { Settings = c.Settings with { StepDefaults = new([new("wait", new StepDefault { OnFailure = FailurePolicy.Ignore })]) } });
        Assert.Same(row, page.StepDefaults.Rows[0]); // the open flyout keeps its row
        Assert.Equal((int)FailurePolicy.Ignore, row.PolicyIndex);
        Assert.True(double.IsNaN(row.TimeoutSeconds));
    }

    [Fact]
    public async Task StartWithWindowsNeedsTheEngine()
    {
        using var page = new SettingsPageViewModel(_session, _engine, a => a(), "logs");

        await page.RefreshAutostartAsync();

        Assert.False(page.CanChangeStartWithWindows);
        Assert.Contains("Start Daze Control", page.StartWithWindowsNote, StringComparison.Ordinal);

        _engine.Answer = r => new PipeMessage { Id = r.Id, Ok = true, Autostart = new AutostartInfo { State = r.Enable == true ? "on" : "off" } };
        _engine.SetRunning(true);

        Assert.True(page.CanChangeStartWithWindows);
        Assert.False(page.StartWithWindows);

        page.StartWithWindows = true;

        Assert.True(_engine.Requests[^1].Enable);
        Assert.True(page.StartWithWindows);
    }

    [Fact]
    public void TheShellFollowsTheEngine()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), "logs");
        Assert.True(shell.EngineMissing);

        _engine.SetRunning(true);

        Assert.True(shell.EngineRunning);
        Assert.Contains("Engine: running", shell.Details("2.5.1"), StringComparison.Ordinal);
    }

    [Fact]
    public void AUsedByFlowFromTheLibraryOpensInTheEditor()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), "logs") { Page = EditorPage.Library };

        shell.Library.OpenFlowCommand.Execute(new LibraryPage.FlowLink("tv", "TV"));

        Assert.Equal(("tv", EditorPage.Flows), (shell.Editor?.FlowId, shell.Page));
    }

    [Fact]
    public void TheSubtitleIsThePageName()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), "logs");

        shell.Page = EditorPage.Settings;

        Assert.Equal("Settings", shell.Subtitle);
    }

    [Theory]
    [InlineData("E7F4", "")]
    [InlineData(null, FlowItemViewModel.DefaultGlyph)]
    [InlineData("nope", FlowItemViewModel.DefaultGlyph)]
    [InlineData("D800", FlowItemViewModel.DefaultGlyph)]
    public void GlyphsAreHexCodePoints(string? code, string expected) =>
        Assert.Equal(expected, FlowItemViewModel.DecodeGlyph(code));
}
