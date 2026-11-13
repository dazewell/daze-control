using DazeControl.Core.Model;
using DazeControl.Core.Yaml;
using DazeControl.Ipc;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

/// <summary>T25.1 (D-150): "Only run if" holds back automatic starts quietly; a check that can't answer lets the run start.</summary>
public sealed class FlowGuardTests
{
    private readonly FlagCheck _flags = new();
    private readonly List<string> _log = [];
    private readonly Lock _logLock = new();

    private void Log(string line)
    {
        lock (_logLock)
            _log.Add(line);
    }

    /// <summary><c>flag.check</c> answers from a table: true/false = the flag; "slow" never answers; "broken" fails to read.</summary>
    private sealed class FlagCheck : IStepProvider
    {
        public readonly Dictionary<string, string> State = [];
        public int Reads;

        public string Id => "flag.check";

        public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            var name = context.Node.With.TryGetValue("name", out var p) && p is ScalarParam s ? s.Text : "";
            switch (State.GetValueOrDefault(name, "false"))
            {
                case "slow":
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
                case "broken":
                    throw new StepFailedException("couldn't read the flags");
                case "true":
                    return;
                default:
                    throw new CheckNotMetException($"Flag \"{name}\" is off.");
            }
        }
    }

    private static Flow Load(string onlyIf, bool byHand = false)
    {
        var yaml = $"flows:\n  f:\n    onlyIfByHand: {(byHand ? "true" : "false")}\n    onlyIf:\n{onlyIf}    nodes:\n    - id: a\n      step: notify\n      with: {{ text: Hi }}\n";
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return load.Config!.Flows[0];
    }

    private static string Check(string flag, string timeout = "5s") => $"    - step: flag.check\n      timeout: {timeout}\n      with: {{ name: {flag} }}\n";

    private FlowGuard Guard() => new(new FlowRunner([_flags], TimeProvider.System), Log);

    [Fact]
    public async Task AllChecksMustHold()
    {
        var flow = Load(Check("home") + Check("awake"));
        var guard = Guard();
        _flags.State["home"] = "true";

        var held = await guard.HeldBackAsync(flow, Library.Empty, "Schedule", TestContext.Current.CancellationToken);
        Assert.Equal("Held back: Flag \"awake\" is off.", held);
        Assert.Equal(held, guard.LastHeldBack("f")!.Sentence);
        Assert.Equal("Schedule", guard.LastHeldBack("f")!.Trigger);

        _flags.State["awake"] = "true";
        Assert.Null(await guard.HeldBackAsync(flow, Library.Empty, "Schedule", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASlowCheckTimesOutAndLetsItStart()
    {
        _flags.State["wifi"] = "slow";
        var flow = Load(Check("wifi", "200ms"));

        Assert.Null(await Guard().HeldBackAsync(flow, Library.Empty, "Schedule", TestContext.Current.CancellationToken).WaitAsync(FlowRunnerTests.Hung, TestContext.Current.CancellationToken));
        Assert.Contains(_log, l => l.Contains("couldn't read if1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACheckThatCantReadLetsItStartButANoStillHolds()
    {
        _flags.State["x"] = "broken";
        Assert.Null(await Guard().HeldBackAsync(Load(Check("x")), Library.Empty, "Event", TestContext.Current.CancellationToken));
        Assert.NotNull(await Guard().HeldBackAsync(Load(Check("x") + Check("off")), Library.Empty, "Event", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HandStartsReadTheListOnlyWhenTheFlowSaysSo()
    {
        Assert.True(FlowGuard.Applies(Load(Check("a")), byHand: false));
        Assert.False(FlowGuard.Applies(Load(Check("a")), byHand: true));
        Assert.True(FlowGuard.Applies(Load(Check("a"), byHand: true), byHand: true));
    }

    [Fact]
    public async Task AHundredHeldBackEventsAddNoHistoryRowsAndNoRuns()
    {
        var history = new RunHistory(null);
        var steps = new FakeProvider();
        var launcher = new FlowLauncher(new FlowRunner([steps], TimeProvider.System), history);
        var source = new Source();
        var flow = Load(Check("home")) with { Triggers = [new EventTrigger("session.unlock")] };
        var flows = new Flows(flow);
        using var service = new TriggerService([source], launcher, flows, TimeProvider.System, () => false, log: Log,
            options: new TriggerOptions(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5)))
        { Guard = Guard() };
        service.Apply([flow]);

        for (var i = 0; i < 100; i++)
            source.Fire();
        await service.IdleAsync();

        Assert.Empty(history.Recent);
        Assert.Empty(steps.Attempts);
        Assert.Equal(100, _flags.Reads);
        Assert.Equal(100, _log.Count(l => l.Contains("Held back", StringComparison.Ordinal)));

        // The flag goes on: the next event starts the flow.
        _flags.State["home"] = "true";
        var done = new TaskCompletionSource();
        launcher.RunCompleted += _ => done.TrySetResult();
        source.Fire();
        await done.Task.WaitAsync(FlowRunnerTests.Hung, TestContext.Current.CancellationToken);
        Assert.Single(history.Recent);
    }

    [Fact]
    public async Task SchedulesAreHeldBackAndHandStartsOnlyWhenAsked()
    {
        const string yaml = """
            flows:
              auto:
                onlyIf: [{ step: flag.check, with: { name: home } }]
                nodes: [{ id: a, step: notify, with: { text: x } }]
              always:
                onlyIf: [{ step: flag.check, with: { name: home } }]
                onlyIfByHand: true
                nodes: [{ id: a, step: notify, with: { text: x } }]
            """;
        var history = new RunHistory(null);
        var launcher = new FlowLauncher(new FlowRunner([new FakeProvider()], TimeProvider.System), history);
        var snapshot = ConfigHost.Apply(ConfigSnapshot.Empty, ConfigYaml.Load(yaml));
        var root = @"Software\DazeControl.Tests\" + Guid.NewGuid().ToString("N");
        var controller = new EngineController(() => snapshot, launcher, history, EngineController.CreateDryRunner(TimeProvider.System),
            new Autostart(root + @"\Run", root + @"\Approved"), new EngineInfo("1.2.3", 42, @"C:\x\DazeControl.exe", "c.yaml"), Log)
        { Guard = Guard() };

        Assert.Null(await controller.TriggerScheduledAsync("auto", "Every day at 08:00"));
        Assert.NotNull(await controller.TriggerAsync("auto", "Tray")); // a person's start ignores the list unless the flow says so

        var refused = await controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.Run, Flow = "always", Wait = true }, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal("Held back: Flag \"home\" is off.", refused.Error);
        var listed = await controller.HandleAsync(new PipeRequest { Id = 8, Op = Ops.List }, CancellationToken.None);
        Assert.Equal("Held back: Flag \"home\" is off.", listed.Flows!.Single(f => f.Id == "always").HeldBack);
        Assert.Null(listed.Flows!.Single(f => f.Id == "auto").HeldBack); // it ran after being held back
        Assert.Single(history.Recent); // only the tray run
    }

    private sealed class Source : ITriggerSource
    {
        public IReadOnlyList<string> Events => ["session.unlock"];

        public event Action<TriggerEvent>? Raised;

        public void Fire() => Raised?.Invoke(new TriggerEvent("session.unlock", null, null, null));

        public void Load()
        {
        }

        public void Unload()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Flows(Flow flow) : IFlowStarter
    {
        public CalledFlow Resolve(string flowId) => new(flow, Library.Empty, flow.Name, null);

        public bool IsRunning(string flowId) => false;

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName) =>
            throw new NotSupportedException();
    }
}
