using DazeControl.Core.Runs;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Runs;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class RunOverlayViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly RunOverlayViewModel _run;
    private readonly Guid _runId = Guid.NewGuid();

    public RunOverlayViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
        _engine.Answer = _ => new PipeMessage { Ok = true };
        _run = new RunOverlayViewModel(_canvas, _engine, action => action());
    }

    public void Dispose()
    {
        _run.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private void Raise(string kind, string? node = null, NodeResult? result = null, FlowOutcome? outcome = null, string flow = "f") =>
        _engine.Raise(new PipeEvent { Kind = kind, RunId = _runId, FlowId = flow, NodeId = node, Node = result, Outcome = outcome });

    private NodeRunState State(string id) => _canvas.Find(id)!.RunState;

    [Fact]
    public void ALiveRunShowsStatesCountsStepsAndLocksEditing()
    {
        Raise(EventKinds.RunStarted);

        Assert.True(_run.IsRunning);
        Assert.False(_canvas.CanEdit);
        Assert.Equal("Running · 0 of 6", _run.Status);
        Assert.Equal("You can edit again when this run finishes.", _run.InfoMessage);
        Assert.Equal(NodeRunState.Pending, State("bt"));
        Assert.Equal(NodeRunState.None, State("both")); // a card has no ring

        Raise(EventKinds.NodeStarted, "bt");
        Assert.Equal(NodeRunState.Running, State("bt"));

        Raise(EventKinds.NodeFinished, result: new NodeResult("bt", NodeOutcome.Succeeded, Duration: TimeSpan.FromSeconds(1.2)));
        Assert.Equal(NodeRunState.Succeeded, State("bt"));
        Assert.Equal("1.2 s", _canvas.Find("bt")!.ShownSummary); // the summary becomes its duration
        Assert.Equal("Running · 1 of 6", _run.Status);
        Assert.True(_canvas.Edges.Single(e => e.From == "bt" && e.To == "vol").IsLit);
        Assert.False(_canvas.Edges.Single(e => e.From == "out").IsLit);
    }

    [Fact]
    public void EditsWaitUntilTheRunFinishes()
    {
        Raise(EventKinds.RunStarted);
        _canvas.Edit(f => f with { Name = "Changed" });
        Assert.Equal("Connect Technics", _canvas.Flow!.Name);

        Raise(EventKinds.RunFinished, outcome: FlowOutcome.Succeeded);
        _canvas.Edit(f => f with { Name = "Changed" });

        Assert.Equal("Changed", _canvas.Flow!.Name);
    }

    [Fact]
    public void AFinishedRunSaysHowItWentAndStaysUntilClosed()
    {
        Raise(EventKinds.RunStarted);
        Raise(EventKinds.NodeFinished, result: new NodeResult("out", NodeOutcome.FailedIgnored, Error: "No such device"));
        Raise(EventKinds.RunFinished, outcome: FlowOutcome.PartiallySucceeded);

        Assert.False(_run.IsRunning);
        Assert.True(_canvas.CanEdit);
        Assert.Equal(RunInfoSeverity.Warning, _run.InfoSeverity);
        Assert.Equal("Done, with a problem. Set default device didn't work, so it went on without it.", _run.InfoMessage);
        Assert.Equal(NodeRunState.FailedMayFail, State("out"));
        Assert.Equal(NodeRunState.Skipped, State("note")); // never reported: the run ended without it

        _run.CloseInfo();

        Assert.False(_run.IsInfoOpen);
        Assert.Equal(NodeRunState.None, State("out"));
    }

    [Fact]
    public void OtherFlowsRunsAreIgnored()
    {
        Raise(EventKinds.RunStarted, flow: "other");

        Assert.False(_run.IsRunning);
        Assert.True(_canvas.CanEdit);
    }

    [Fact]
    public void ARunAlreadyGoingShowsFromItsNextStep()
    {
        Raise(EventKinds.NodeStarted, "vol");

        Assert.True(_run.IsRunning);
        Assert.Equal(NodeRunState.Running, State("vol"));
    }

    [Fact]
    public void TheEngineGoingAwayUnlocksEditing()
    {
        Raise(EventKinds.RunStarted);

        _engine.SetRunning(false);

        Assert.False(_run.IsRunning);
        Assert.True(_canvas.CanEdit);
        Assert.Equal(RunInfoSeverity.Warning, _run.InfoSeverity);
    }

    [Fact]
    public async Task RunAndStopGoToTheEngine()
    {
        await _run.RunCommand.ExecuteAsync(null);
        Raise(EventKinds.RunStarted);
        await _run.StopCommand.ExecuteAsync(null);

        Assert.Equal([(Ops.Run, "f", "Editor"), (Ops.Cancel, "f", null)],
            _engine.Requests.Select(r => (r.Op, r.Flow, r.Trigger)));
    }

    [Fact]
    public async Task ARefusedRunSaysWhy()
    {
        _engine.Answer = r => PipeMessage.Fail(r.Id, "Connect Technics needs fixing before it can run.");

        await _run.RunCommand.ExecuteAsync(null);

        Assert.Equal(RunInfoSeverity.Error, _run.InfoSeverity);
        Assert.Equal("Connect Technics needs fixing before it can run.", _run.InfoMessage);
    }

    [Fact]
    public async Task DryRunSendsTheFlowOnScreenAndNumbersStepsInStartOrder()
    {
        _engine.Answer = r => new PipeMessage
        {
            Ok = true,
            Run = new RunRecord(Guid.NewGuid(), "f", "Connect Technics", "Editor (dry run)", DateTimeOffset.UnixEpoch, TimeSpan.Zero,
                FlowOutcome.PartiallySucceeded,
                [
                    new NodeResult("bt", NodeOutcome.Succeeded, Order: 1),
                    new NodeResult("out", NodeOutcome.FailedIgnored, Error: "technics isn't in your Library.", Order: 2),
                    new NodeResult("vol", NodeOutcome.Succeeded, Order: 3),
                    new NodeResult("note", NodeOutcome.Succeeded, Order: 4),
                    new NodeResult("both", NodeOutcome.Succeeded, Attempts: 0),
                    new NodeResult("w1", NodeOutcome.Succeeded, Order: 5),
                    new NodeResult("w2", NodeOutcome.Skipped, Attempts: 0),
                ]),
        };

        await _run.DryRunCommand.ExecuteAsync(null);

        var request = _engine.Requests.Single();
        Assert.True(request.DryRun);
        Assert.Contains("Connect Technics", request.Config, StringComparison.Ordinal);
        Assert.Equal("1", _canvas.Find("bt")!.OrderText);
        Assert.Equal("", _canvas.Find("w2")!.OrderText);
        Assert.Equal(NodeRunState.FailedMayFail, State("out"));
        Assert.Equal("technics isn't in your Library.", _canvas.Find("out")!.ShownSummary);
        Assert.True(_canvas.CanEdit); // a dry run doesn't lock
        Assert.Equal("5 steps would start, in the order shown. Set default device would fail: technics isn't in your Library. 1 step wouldn't run.",
            _run.InfoMessage);
    }

    [Fact]
    public void OpenInEditorShowsARecordedRun()
    {
        var run = new RunRecord(Guid.NewGuid(), "f", "Connect Technics", "Alt+Z", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(3),
            FlowOutcome.Failed, [new NodeResult("bt", NodeOutcome.Failed, Error: "Bluetooth didn't connect in 10 s", Duration: TimeSpan.FromSeconds(10))]);

        _run.Show(run);

        Assert.Equal(NodeRunState.Failed, State("bt"));
        Assert.Equal("Didn't work · 10 s", _canvas.Find("bt")!.ShownSummary);
        Assert.StartsWith("Connect Technics didn't finish. Bluetooth didn't connect in 10 s.", _run.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RunStatesSurviveARelayout()
    {
        Raise(EventKinds.RunStarted);
        Raise(EventKinds.NodeStarted, "bt");

        _canvas.ReportSize("bt", 300, 80);

        Assert.Equal(NodeRunState.Running, State("bt"));
    }
}
