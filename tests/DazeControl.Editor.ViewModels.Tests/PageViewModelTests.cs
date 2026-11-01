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
        Assert.Equal(FlowItemViewModel.DefaultGlyph, technics.Glyph);
        Assert.True(technics.IsEnabled);
        Assert.False(page.IsEmpty);
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
        Assert.StartsWith("Failed", page.Items[1].LastRun, StringComparison.Ordinal);

        history = [Run(FlowOutcome.Failed, 0), Run(FlowOutcome.Succeeded, 5)];
        _engine.Raise(new PipeEvent { Kind = EventKinds.RunFinished, FlowId = "tv" });

        Assert.StartsWith("Worked", page.Items[1].LastRun, StringComparison.Ordinal);
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
    public void TheSubtitleIsThePageName()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), "logs");

        shell.Page = EditorPage.Settings;

        Assert.Equal("Settings", shell.Subtitle);
    }
}
