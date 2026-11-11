using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Menus;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;

namespace DazeControl.Runtime.Tests;

/// <summary><c>prompt.ask</c> and ask-at-start inputs at run time (M20, D-107), with a fake presenter: no window.</summary>
public sealed class PromptRunTests : IDisposable
{
    private const string Library = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }

        """;

    private readonly ManualTime _time = new();
    private readonly FakePrompt _presenter = new();
    private readonly Dictionary<string, bool> _flags = [];
    private readonly List<string> _devices = [];

    public void Dispose() => _time.Dispose();

    private FlowRunner Runner() => new([new PromptProvider(_presenter, (name, on) => _flags[name] = on), new Recorder("notify", _ => { }),
        new Recorder("audio.setDefault", c => _devices.Add(((ScalarParam)c.Node.With["device"]).Text))], _time);

    private static (Flow Flow, Library Library) Load(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        Assert.DoesNotContain(DazeControl.Core.Validation.ConfigValidator.Validate(load.Config!), p => p.Severity == DazeControl.Core.Validation.Severity.Error);
        return (load.Config!.Flows[0], load.Config.Library);
    }

    private static (Flow Flow, Library Library) Ask(string with, string extra = "") => Load(Library + $$"""
        flows:
          f:
            nodes:
              - { id: q, step: prompt.ask, with: { {{with}} }{{extra}} }
              - { id: yes, step: notify, with: { text: x }, after: [q] }
              - { id: no, step: notify, with: { text: x }, after: [{ id: q, when: failure }] }

        """);

    private async Task<RunResult> Run((Flow Flow, Library Library) loaded, string trigger = "Tray", CancellationToken? token = null) =>
        await Bound(Runner().RunAsync(loaded.Flow, loaded.Library, [], token ?? TestContext.Current.CancellationToken, trigger));

    private static NodeResult Node(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id);

    [Fact]
    public async Task YesGoesOnAndTurnsTheFlagOn()
    {
        _presenter.Script = _ => new("yes", MenuCloseReason.Picked);
        var result = await Run(Ask("question: Go on?, flag: ok"));

        Assert.Equal(NodeOutcome.Succeeded, Node(result, "q").Outcome);
        Assert.Equal("Answered Yes", Node(result, "q").Summary);
        Assert.Equal(NodeOutcome.NotNeeded, Node(result, "no").Outcome);
        Assert.True(_flags["ok"]);
        var shown = Assert.Single(_presenter.Shown);
        Assert.Equal(("Go on?", TimeSpan.FromSeconds(30)), (shown.Title, shown.CloseAfter));
        Assert.Equal(["yes", "no"], shown.Items.Select(i => i.FlowId));
    }

    [Fact]
    public async Task NoFailsTheStepSoAWhenItFailsBranchRuns()
    {
        _presenter.Script = _ => new("no", MenuCloseReason.Picked);
        var result = await Run(Ask("question: Go on?, flag: ok"));

        Assert.Equal(NodeOutcome.Failed, Node(result, "q").Outcome);
        Assert.Equal("You answered No.", Node(result, "q").Error);
        Assert.Equal(NodeOutcome.Succeeded, Node(result, "no").Outcome);
        Assert.False(_flags["ok"]);
    }

    [Fact]
    public async Task ChooseTurnsOnOnlyTheChosenAnswersFlag()
    {
        _presenter.Script = r => new(r.Items[1].FlowId, MenuCloseReason.Picked);
        var result = await Run(Ask("mode: choose, question: Which?, choices: [Tea, Coffee, Water], flag: drink, default: Tea"));

        Assert.Equal("Chose Coffee", Node(result, "q").Summary);
        Assert.Equal(new Dictionary<string, bool> { ["drink:Tea"] = false, ["drink:Coffee"] = true, ["drink:Water"] = false }, _flags);
        Assert.Equal("0", _presenter.Shown[0].LastPickedFlowId); // the default is focused first
    }

    [Fact]
    public async Task NoAnswerStopsByDefaultWithTheNote()
    {
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        var result = await Run(Ask("question: Go on?"));

        Assert.True(result.Quiet);
        Assert.Equal(NodeOutcome.Cancelled, Node(result, "q").Outcome);
        Assert.Equal("No answer", Node(result, "q").Error);
        Assert.Equal(NodeOutcome.Cancelled, Node(result, "yes").Outcome);
    }

    [Fact]
    public async Task NoAnswerCanUseTheDefaultOrCarryOn()
    {
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        var useDefault = await Run(Ask("question: Go on?, onTimeout: use-default, default: yes, flag: ok"));
        Assert.Equal(NodeOutcome.Succeeded, Node(useDefault, "q").Outcome);
        Assert.True(_flags["ok"]);

        var useNo = await Run(Ask("question: Go on?, onTimeout: use-default, default: no"));
        Assert.Equal(NodeOutcome.Failed, Node(useNo, "q").Outcome);

        var skip = await Run(Ask("question: Go on?, onTimeout: skip"));
        Assert.Equal(NodeOutcome.Succeeded, Node(skip, "q").Outcome);
        Assert.Equal("No answer, carried on", Node(skip, "q").Summary);
        Assert.False(skip.Quiet);
    }

    [Fact]
    public async Task AScheduledStartWithNoAnswerStopsEvenWhenAPersonWouldGetTheDefault()
    {
        // Unset onTimeout is stop for every start; a written use-default is honoured for a schedule too.
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        Assert.True((await Run(Ask("question: Go on?"), "Schedule · 22:30")).Quiet);
        Assert.Equal(NodeOutcome.Succeeded, Node(await Run(Ask("question: Go on?, onTimeout: use-default, default: yes"), "Schedule · 22:30"), "q").Outcome);
    }

    [Fact]
    public async Task ClosingTheMenuEndsTheFlowQuietly()
    {
        _presenter.Script = _ => new(null, MenuCloseReason.Cancelled);
        var result = await Run(Ask("question: Go on?"));
        Assert.True(result.Quiet);
        Assert.Equal("Not answered", Node(result, "q").Error);
    }

    [Fact]
    public async Task StoppingTheFlowClosesThePromptAndCancelsTheRun()
    {
        _presenter.WaitForToken = true;
        using var cancel = new CancellationTokenSource();
        var run = Run(Ask("question: Go on?"), token: cancel.Token);
        await _presenter.Opened.Task.WaitAsync(Hung, TestContext.Current.CancellationToken);

        await cancel.CancelAsync();
        var result = await run;

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        await _presenter.Cancelled.Task.WaitAsync(Hung, TestContext.Current.CancellationToken); // the runner doesn't wait for the window to close
    }

    [Fact]
    public async Task ADryRunAssumesTheDefaultAndShowsNothing()
    {
        var dry = EngineController.CreateDryRunner(_time);
        var (flow, library) = Ask("mode: choose, question: Which?, choices: [Tea, Coffee], default: Coffee");
        var result = await Bound(dry.RunAsync(flow, library, TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal("Would ask \"Which?\", assuming Coffee", Node(result, "q").Summary);
        Assert.Empty(_presenter.Shown);
        var confirm = await Bound(dry.RunAsync(Ask("question: Go on?").Flow, library, TestContext.Current.CancellationToken));
        Assert.Equal("Would ask \"Go on?\", assuming Yes", Node(confirm, "q").Summary);
    }

    // ---- ask: true inputs ----

    private const string Asked = Library + """
        flows:
          f:
            inputs:
              output: { kind: audio, name: Output, default: technics, ask: true, timeout: 20s }
            nodes:
              - { id: set, step: audio.setDefault, with: { device: $output } }

        """;

    private FlowLauncher Launcher(Action<FlowLauncher>? configure = null)
    {
        var launcher = new FlowLauncher(Runner()) { StartPrompts = new InputPrompts(_presenter) };
        configure?.Invoke(launcher);
        return launcher;
    }

    [Fact]
    public async Task TheAnswerFillsTheInputBeforeTheRunStarts()
    {
        _presenter.Script = _ => new("sony", MenuCloseReason.Picked);
        var (flow, library) = Load(Asked);

        var result = await Bound(Launcher().TriggerAsync(flow, library, "Tray"))!;

        Assert.Equal(FlowOutcome.Succeeded, result!.Outcome);
        Assert.Equal(["sony"], _devices);
        var request = Assert.Single(_presenter.Shown);
        Assert.Equal(("Output", TimeSpan.FromSeconds(20), "technics"), (request.Title, request.CloseAfter, request.LastPickedFlowId));
        Assert.Equal(["technics", "sony"], request.Items.Select(i => i.FlowId));
    }

    [Fact]
    public async Task NoAnswerUsesTheDefaultDeviceForAPersonAndStopsForASchedule()
    {
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        var (flow, library) = Load(Asked);
        var launcher = Launcher();

        var byPerson = await Bound(launcher.TriggerAsync(flow, library, "Tray"))!;
        Assert.Equal(FlowOutcome.Succeeded, byPerson!.Outcome);
        Assert.Equal(["technics"], _devices);

        var scheduled = await Bound(launcher.TriggerAsync(flow, library, "Schedule · 22:30"))!;
        Assert.Equal(FlowOutcome.Cancelled, scheduled!.Outcome);
        Assert.True(scheduled.Quiet);
        Assert.Equal(Prompts.NoAnswer, scheduled.Nodes.Single().Error);
        Assert.Equal(["technics"], _devices); // the scheduled run never started
        Assert.False(launcher.IsRunning("f"));
    }

    [Fact]
    public async Task AWrittenStopStopsAPersonsRunToo()
    {
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        var (flow, library) = Load(Asked.Replace("timeout: 20s", "timeout: 20s, onTimeout: stop", StringComparison.Ordinal));
        var result = await Bound(Launcher().TriggerAsync(flow, library, "Tray"))!;
        Assert.Equal(FlowOutcome.Cancelled, result!.Outcome);
        Assert.Empty(_devices);
    }

    [Fact]
    public async Task StoppingTheRunWhileItAsksFreesTheSlot()
    {
        _presenter.WaitForToken = true;
        var (flow, library) = Load(Asked);
        var launcher = Launcher();
        var run = launcher.TriggerAsync(flow, library, "Tray");
        await _presenter.Opened.Task.WaitAsync(Hung, TestContext.Current.CancellationToken);
        Assert.True(launcher.IsRunning("f"));

        Assert.True(launcher.Cancel("f"));
        var result = await Bound(run)!;

        Assert.Equal(FlowOutcome.Cancelled, result!.Outcome);
        Assert.False(launcher.IsRunning("f"));
        Assert.Empty(_devices);
    }

    [Fact]
    public async Task AnInputACallerPassedIsNotAskedAgain()
    {
        var (flow, library) = Load(Asked);
        var resolved = FlowInputs.Apply(flow, new Dictionary<string, string> { ["output"] = "sony" }, keepAsked: true);

        var result = await Bound(Launcher().TriggerAsync(resolved, library, "Flow · Caller"))!;

        Assert.Equal(FlowOutcome.Succeeded, result!.Outcome);
        Assert.Empty(_presenter.Shown);
        Assert.Equal(["sony"], _devices);
    }

    [Fact]
    public async Task NothingToChooseBetweenAsksNothingAndWithoutAWindowTheDefaultRuns()
    {
        var (flow, _) = Load(Asked);
        var one = new Library { Audio = [new AudioDevice("technics", "Technics")] };
        Assert.Equal(FlowOutcome.Succeeded, (await Bound(Launcher().TriggerAsync(flow, one, "Tray")))!.Outcome);
        Assert.Empty(_presenter.Shown);

        var plain = new FlowLauncher(Runner()); // no StartPrompts: the default, never a stuck "$output"
        Assert.Equal(FlowOutcome.Succeeded, (await Bound(plain.TriggerAsync(flow, Load(Asked).Library, "Tray")))!.Outcome);
        Assert.Equal(["technics", "technics"], _devices);
    }

    internal sealed class Recorder(string id, Action<StepContext> run) : IStepProvider
    {
        public string Id => id;

        public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            run(context);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakePrompt : IMenuPresenter
    {
        public Func<MenuRequest, MenuOutcome>? Script { get; set; }

        /// <summary>Waits until the token fires, like the real window with nobody there.</summary>
        public bool WaitForToken { get; set; }

        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<MenuRequest> Shown { get; } = [];

        public async Task<MenuOutcome> ShowAsync(MenuRequest request, CancellationToken cancellationToken)
        {
            lock (Shown)
                Shown.Add(request);
            Opened.TrySetResult();
            if (WaitForToken)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
            return Script!(request);
        }
    }
}
