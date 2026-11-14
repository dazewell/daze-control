using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Ipc;
using CtrlAltDaze.Runtime.Control;
using CtrlAltDaze.Runtime.Lifecycle;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>Step defaults reach runs, dry runs and step tests (D-080, M14 T14.2).</summary>
public sealed class StepDefaultsRunTests : IDisposable
{
    private static string Config(string defaults, string step = "{ id: a, step: notify, with: { text: x } }") => $$"""
        settings:
          stepDefaults:
            {{defaults}}
        flows:
          f:
            name: F
            nodes:
              - {{step}}
              - { id: b, step: notify, with: { text: y }, after: [a] }
        """;

    private readonly string _root = @"Software\CtrlAltDaze.Tests\" + Guid.NewGuid().ToString("N");
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly RunHistory _history = new(null);
    private readonly FlowLauncher _launcher;
    private readonly EngineController _controller;
    private ConfigSnapshot _snapshot = ConfigSnapshot.Empty;

    public StepDefaultsRunTests()
    {
        _launcher = new FlowLauncher(new FlowRunner([_provider], _time), _history);
        _controller = new EngineController(() => _snapshot, _launcher, _history, EngineController.CreateDryRunner(_time),
            new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, @"C:\x\CtrlAltDaze.exe", "c.yaml"),
            _ => { }, stepRunner: new FlowRunner([_provider], _time));
    }

    public void Dispose()
    {
        _time.Dispose();
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    private void Load(string yaml) => _snapshot = ConfigHost.Apply(_snapshot, ConfigYaml.Load(yaml));

    private static NodeResult Node(RunResult? result, string id) => result!.Nodes.First(n => n.NodeId == id);

    [Fact]
    public async Task ATypeDefaultTimeoutFailsAHangingStep()
    {
        Load(Config("notify: { timeout: 1s }"));
        _provider.On("a", (_, token) => FakeProvider.Hang(token));
        var run = _controller.TriggerAsync("f", "t");

        await _provider.Started("a");
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(run.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await FlowRunnerTests.Bound(run);

        Assert.Equal("Didn't finish within 1s.", Node(result, "a").Error);
    }

    [Fact]
    public async Task AnExplicitStepTimeoutBeatsTheDefault()
    {
        Load(Config("notify: { timeout: 1s }", "{ id: a, step: notify, timeout: 5s, with: { text: x } }"));
        _provider.On("a", (_, token) => FakeProvider.Hang(token));
        var run = _controller.TriggerAsync("f", "t");

        await _provider.Started("a");
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(run.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Didn't finish within 5s.", Node(await FlowRunnerTests.Bound(run), "a").Error);
    }

    [Fact]
    public async Task ARetryDefaultRetriesAFailingStep()
    {
        Load(Config("notify: { retry: { count: 2 } }"));
        _provider.On("a", (_, _) => FakeProvider.Fail("no"));

        var result = await FlowRunnerTests.Bound(_controller.TriggerAsync("f", "t"));

        Assert.Equal(3, Node(result, "a").Attempts);
    }

    [Fact]
    public async Task ASkipDefaultSkipsDependentsAndLetsOtherBranchesRun()
    {
        Load("""
            settings:
              stepDefaults:
                notify: { onFailure: skip }
            flows:
              f:
                name: F
                nodes:
                  - { id: a, step: notify, with: { text: x } }
                  - { id: b, step: notify, with: { text: y }, after: [a] }
                  - { id: c, step: notify, with: { text: z } }
            """);
        _provider.On("a", (_, _) => FakeProvider.Fail("no"));

        var result = await FlowRunnerTests.Bound(_controller.TriggerAsync("f", "t"));

        Assert.Equal(NodeOutcome.Failed, Node(result, "a").Outcome);
        Assert.Equal(NodeOutcome.Skipped, Node(result, "b").Outcome);
        Assert.Equal(NodeOutcome.Succeeded, Node(result, "c").Outcome);
    }

    [Fact]
    public async Task ADefaultEditedMidRunChangesOnlyTheNextRun()
    {
        Load(Config("notify: { retry: { count: 1 } }"));
        var release = new TaskCompletionSource();
        _provider.On("a", (_, token) => release.Task.WaitAsync(token).ContinueWith(_ => FakeProvider.Fail("no"), token).Unwrap());
        var first = _controller.TriggerAsync("f", "t");
        await _provider.Started("a");

        Load(Config("notify: { retry: { count: 0 } }"));
        release.SetResult();
        var firstResult = await FlowRunnerTests.Bound(first);
        var second = await FlowRunnerTests.Bound(_controller.TriggerAsync("f", "t"));

        Assert.Equal(2, Node(firstResult, "a").Attempts);
        Assert.Equal(1, Node(second, "a").Attempts);
    }

    [Fact]
    public async Task ADryRunAndAStepTestUseTheSentDefaults()
    {
        var yaml = Config("notify: { timeout: 1s }");
        _provider.On("a", (_, token) => FakeProvider.Hang(token));

        var test = _controller.HandleAsync(new PipeRequest { Id = 5, Op = Ops.TestStep, Flow = "f", Node = "a", Config = yaml },
            CancellationToken.None);
        await _provider.Started("a");
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var answer = await FlowRunnerTests.Bound(test);
        Assert.Equal("Didn't finish within 1s.", answer.Run!.Nodes.Single().Error);

        var dry = await _controller.HandleAsync(new PipeRequest { Id = 6, Op = Ops.Run, Flow = "f", DryRun = true, Config = yaml },
            CancellationToken.None);
        Assert.True(dry.Ok, dry.Error);
    }
}
