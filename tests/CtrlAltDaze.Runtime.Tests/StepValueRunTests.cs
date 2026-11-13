using System.Globalization;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Runtime.Providers;
using static CtrlAltDaze.Runtime.Tests.FlowRunnerTests;
using static CtrlAltDaze.Runtime.Tests.PromptRunTests;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>Step values at run time (D-129, T23.2): what steps give, who sees it, when it is missing. Fake providers, no devices.</summary>
public sealed class StepValueRunTests : IDisposable
{
    private const string Header = """
        library:
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC", name: Technics }
            sony: { address: "B8:20:8E:ED:48:FD", name: Sony }
        flows:
          f:
            name: Evening

        """;

    private readonly ManualTime _time = new();
    private readonly List<string> _said = [];

    public void Dispose() => _time.Dispose();

    private static (Flow Flow, Library Library) Load(string inputs, params string[] nodes)
    {
        var load = ConfigYaml.Load(Header + inputs + "    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n")));
        Assert.Empty(load.Problems);
        Assert.DoesNotContain(CtrlAltDaze.Core.Validation.ConfigValidator.Validate(load.Config!), p => p.Severity == CtrlAltDaze.Core.Validation.Severity.Error);
        return (load.Config!.Flows[0], load.Config.Library);
    }

    private static string Connect(string id, string device) => $"{{ id: {id}, step: bluetooth.connect, with: {{ device: {device} }} }}";

    private static string Say(string text, string after, string id = "note") =>
        $"{{ id: {id}, step: notify, with: {{ text: \"{text}\" }}, after: {after} }}";

    private FlowRunner Runner(Action<StepContext>? connect = null, Action<StepContext>? run = null) => new(
    [
        new Recorder("bluetooth.connect", connect ?? (c => c.SetDevice("device", ((ScalarParam)c.Node.With["device"]).Text))),
        new Recorder("process.run", run ?? (_ => { })),
        new Recorder("notify", c => { lock (_said) _said.Add(((ScalarParam)c.Node.With["text"]).Text); }),
    ], _time) { DryRun = false };

    private static async Task<RunResult> Run(FlowRunner runner, (Flow Flow, Library Library) loaded) =>
        await Bound(runner.RunAsync(loaded.Flow, loaded.Library, [], TestContext.Current.CancellationToken, "Alt+Z"));

    /// <summary>Lets the one retry's delay pass (the attempt timeout is armed first, then the delay).</summary>
    private async Task Retry(Task run)
    {
        await _time.WaitForTimerAsync();
        await _time.WaitForTimerAsync();
        Assert.False(run.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AMessageNamesTheDeviceAStepConnected()
    {
        var result = await Run(Runner(), Load("", Connect("bt", "technics"), Say("Connected to {bt.device} at {@time} by {@trigger} in {@flow}", "[bt]")));

        var now = _time.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal([$"Connected to Technics at {now} by Alt+Z in Evening"], _said);
    }

    [Fact]
    public async Task AMessageNamesTheDeviceOrNetworkThatStartedTheRun()
    {
        var (flow, library) = Load("", Say("Plugged in: {@event}.", "[]"));
        var runner = Runner();

        await Bound(runner.RunAsync(flow, library, [], TestContext.Current.CancellationToken, "Event · device connected", eventDetail: "Xbox Wireless Controller"));
        await Run(runner, (flow, library)); // started by a person: nothing to name

        Assert.Equal(["Plugged in: Xbox Wireless Controller.", "Plugged in: ."], _said);
    }

    [Fact]
    public async Task ParallelBranchesEachKeepTheirOwnValue()
    {
        await Run(Runner(), Load("", Connect("a", "technics"), Connect("b", "sony"), Say("{a.device} + {b.device}", "[a, b]")));

        Assert.Equal(["Technics + Sony"], _said);
    }

    [Fact]
    public async Task ARetryReplacesTheValuesOfTheEarlierAttempt()
    {
        var loaded = Load("", "{ id: p, step: process.run, with: { path: x.exe }, retry: { count: 1, delay: 1s } }",
            Say("code {p.exitCode}", "[p]"));
        var run = Run(Runner(run: c =>
        {
            c.SetNumber("exitCode", c.Attempt == 1 ? 7 : 0);
            if (c.Attempt == 1)
                throw new StepFailedException("first");
        }), loaded);
        await Retry(run);
        var result = await run;

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["code 0"], _said);
    }

    [Fact]
    public async Task AValueOfAnEarlierAttemptIsNotKept()
    {
        var loaded = Load("", "{ id: p, step: process.run, with: { path: x.exe }, retry: { count: 1, delay: 1s }, onFailure: ignore }",
            Say("[{p.exitCode|none}]", "[p]"));
        var run = Run(Runner(run: c =>
        {
            if (c.Attempt == 1)
                c.SetNumber("exitCode", 7);
            throw new StepFailedException("again");
        }), loaded);
        await Retry(run);
        await run;

        Assert.Equal(["[none]"], _said);
    }

    [Fact]
    public async Task AFailedStepKeepsItsErrorAndExitCodeButNotOtherValues()
    {
        var loaded = Load("", "{ id: p, step: process.run, with: { path: x.exe } }", Connect("bt", "technics"),
            Say("{p.error} / {p.exitCode}", "[{ id: p, when: failure }]", "fail"),
            Say("[{bt.device|nothing}]", "[{ id: bt, when: failure }]", "lost"));
        await Run(Runner(connect: c =>
        {
            c.SetDevice("device", "technics");
            throw new StepFailedException("no range");
        }, run: c =>
        {
            c.SetNumber("exitCode", 2);
            throw new StepFailedException("x.exe ended with exit code 2.");
        }), loaded);

        Assert.Contains("x.exe ended with exit code 2. / 2", _said);
        Assert.Contains("[nothing]", _said);
    }

    [Fact]
    public async Task AStepThatDidNotGiveAValueShowsTheFallbackOrNothing()
    {
        var loaded = Load("", "{ id: bt, step: bluetooth.connect, with: { device: technics }, onFailure: ignore }", Say("<{bt.error}><{bt.device|none}>", "[bt]"));
        await Run(Runner(connect: _ => throw new StepFailedException("down")), loaded);

        Assert.Equal(["<down><none>"], _said);
    }

    [Fact]
    public async Task AFlowInputShowsItsDeviceName()
    {
        var loaded = Load("    inputs:\n      headset: { kind: bluetooth, default: sony }\n",
            "{ id: note, step: notify, with: { text: \"Using {$headset}\" } }");
        await Run(Runner(), loaded);

        Assert.Equal(["Using Sony"], _said);
    }

    [Fact]
    public async Task OnlyValuesSomeSettingUsesAreKept()
    {
        var loaded = Load("", Connect("bt", "technics"), Say("plain", "[bt]"));
        var wanted = new List<bool>();
        await Run(new FlowRunner([new Recorder("bluetooth.connect", c => wanted.Add(c.Wants("device"))), new Recorder("notify", _ => { })], _time), loaded);

        Assert.Equal([false], wanted);
    }

    [Fact]
    public async Task ADryRunShowsTitlesNotDevices()
    {
        var loaded = Load("", Connect("bt", "technics"), Say("Connected to {bt.device} at {@time}: {bt.error|none}", "[bt]"));
        var runner = new FlowRunner([new Recorder("bluetooth.connect", _ => { }),
            new Recorder("notify", c => _said.Add(((ScalarParam)c.Node.With["text"]).Text))], _time) { DryRun = true };
        var result = await Run(runner, loaded);

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["Connected to ‹Device› at ‹Time›: ‹Why it failed›"], _said);
    }

    [Fact]
    public async Task ASkippedStepGivesNothing()
    {
        var loaded = Load("", "{ id: bt, step: bluetooth.connect, with: { device: technics }, onFailure: skip }",
            Say("<{bt.device|none}>", "[{ id: bt, when: always }]"));
        await Run(Runner(connect: _ => throw new StepFailedException("down")), loaded);

        Assert.Equal(["<none>"], _said);
    }

    [Fact]
    public async Task APickAndAnAnswerAreValuesAndQuestionsTakeValues()
    {
        var presenter = new FakePrompt { Script = _ => new("sony", CtrlAltDaze.Runtime.Menus.MenuCloseReason.Picked) };
        var loaded = Load("", "{ id: pick, step: prompt.pick, with: { kind: bluetooth } }",
            "{ id: ask, step: prompt.ask, with: { question: \"Use {pick.device}?\" }, after: [pick] }",
            Say("{pick.device}: {ask.answer}", "[ask]"));
        var runner = new FlowRunner([new PickProvider(presenter), new PromptProvider(presenter), new Recorder("notify", c => _said.Add(((ScalarParam)c.Node.With["text"]).Text))], _time);
        presenter.Script = request => request.Title.StartsWith("Use", StringComparison.Ordinal)
            ? new("yes", CtrlAltDaze.Runtime.Menus.MenuCloseReason.Picked)
            : new("sony", CtrlAltDaze.Runtime.Menus.MenuCloseReason.Picked);
        var result = await Run(runner, loaded);

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal("Use Sony?", presenter.Shown[1].Title);
        Assert.Equal(["Sony: Yes"], _said);
    }

    [Fact]
    public async Task AnUnmetOnlyIfPublishesNothing()
    {
        var loaded = Load("", "{ id: bt, step: bluetooth.connect, with: { device: technics }, only: disconnected }", Say("<{bt.device|none}>", "[bt]"));
        var runner = new FlowRunner([new Recorder("bluetooth.connect", c => c.SetDevice("device", "technics")),
            new Recorder("notify", c => _said.Add(((ScalarParam)c.Node.With["text"]).Text))], _time,
            probes: [new Probe("bluetooth.connected", c => { c.SetDevice("device", "technics"); return new FactReading(true, "connected"); })]);
        await Run(runner, loaded);

        Assert.Equal(["<none>"], _said);
    }

    private sealed class Probe(string fact, Func<StepContext, FactReading> read) : IFactProbe
    {
        public string Fact => fact;

        public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken) => Task.FromResult(read(context));
    }
    [Fact]
    public async Task AStepTurnedOffNeverRunsGivesNoValuesAndCountsAsDone()
    {
        // T25.4, D-148: the provider is never called, its value takes the written fallback, the run succeeds.
        var connects = 0;
        var events = new List<RunEvent>();
        var runner = new FlowRunner(
        [
            new Recorder("bluetooth.connect", _ => Interlocked.Increment(ref connects)),
            new Recorder("notify", c => { lock (_said) _said.Add(((ScalarParam)c.Node.With["text"]).Text); }),
        ], _time, events: e => { lock (events) events.Add(e); }) { DryRun = false };
        var loaded = Load("", Connect("bt", "technics")[..^2] + ", enabled: false }", Say("Now on {bt.device|nothing}", "[bt]"));

        var result = await Run(runner, loaded);

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, connects);
        Assert.Equal(["Now on nothing"], _said);
        var off = Assert.Single(result.Nodes, n => n.NodeId == "bt");
        Assert.Equal((NodeOutcome.Succeeded, 0, true), (off.Outcome, off.Attempts, off.TurnedOff));
        Assert.Null(Assert.Single(result.Nodes, n => n.NodeId == "note").TurnedOff);
        lock (events)
        {
            Assert.DoesNotContain(events.OfType<NodeStarted>(), e => e.NodeId == "bt");
            Assert.Contains(events.OfType<NodeFinished>(), e => e.Result.NodeId == "bt");
        }
        var progress = new RunProgress(loaded.Flow);
        Assert.Equal("0 of 1", progress.Text);
        Assert.False(progress.Finish("bt"));
    }
}
