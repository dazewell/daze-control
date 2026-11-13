using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Providers.Basic;
using CtrlAltDaze.Runtime;
using CtrlAltDaze.Runtime.Control;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

/// <summary>
/// flow-calls/DESIGN.md §1's shapes end to end with safe steps (notify, flags): a scratch config.yaml through
/// ConfigHost, the engine's FlowCalls, launcher and history file, as <c>cad run</c> would drive them.
/// </summary>
public sealed class FlowCallScenarioTests : IDisposable
{
    private const string Config = """
        flows:
          tv-ready:
            name: TV ready
            tray: { show: false }
            nodes:
              - { id: wake,  step: notify, with: { text: wake }, retry: { count: 2, delay: 0s } }
              - { id: input, step: notify, with: { text: hdmi1 }, after: [wake] }
          tv-home:
            name: TV home
            nodes:
              - { id: ready, step: flow.run, with: { flow: tv-ready } }
              - { id: home,  step: notify,   with: { text: home }, after: [ready] }
          make-technics:
            name: Make Technics the default
            tray: { show: false }
            nodes:
              - { id: set,     step: flag.check, with: { name: technics-default }, onFailure: skip }
              - { id: connect, step: flag.check, with: { name: technics-near }, after: [{ id: set, when: failure }] }
              - { id: again,   step: notify,     with: { text: technics }, after: [connect] }
          make-sony:
            name: Make Sony the default
            tray: { show: false }
            nodes:
              - { id: set, step: notify, with: { text: sony } }
          headphones:
            name: Headphones
            nodes:
              - { id: technics, step: flow.run, with: { flow: make-technics }, onFailure: skip }
              - { id: sony,     step: flow.run, with: { flow: make-sony }, after: [{ id: technics, when: failure }] }
          kick-off:
            name: Kick off
            nodes:
              - { id: start, step: flow.run, with: { flow: tv-home, wait: false } }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-flowcall-" + Guid.NewGuid().ToString("N"));
    private readonly FlagStore _flags = new();
    private readonly List<string> _shown = [];
    private readonly ConfigHost _config;
    private readonly RunHistory _history;
    private readonly FlowLauncher _launcher;

    public FlowCallScenarioTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.yaml"), Config);
        _config = new ConfigHost(Path.Combine(_dir, "config.yaml"), TimeProvider.System);
        _config.Reload();
        var calls = new FlowCalls(() => _config.Current);
        IStepProvider[] providers =
        [
            new NotifyProvider(new Recorder(_shown)), new FlagSetProvider(_flags), new FlagCheckProvider(_flags), new FlowProvider(calls),
        ];
        var runner = new FlowRunner(providers, TimeProvider.System) { Flows = calls };
        _history = new RunHistory(Path.Combine(_dir, "history.jsonl"));
        _launcher = new FlowLauncher(runner, _history);
        calls.Launcher = _launcher;
    }

    public void Dispose()
    {
        _config.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<RunResult> Run(string flowId)
    {
        var state = _config.Current.Flows.Single(f => f.FlowId == flowId);
        var result = await _launcher.TriggerAsync(state.Running!, state.Library, "Command line").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return result!;
    }

    [Fact]
    public void TheConfigIsValid() =>
        Assert.DoesNotContain(_config.Current.Problems, p => p.Severity == Core.Validation.Severity.Error);

    [Fact]
    public async Task TvHomeRunsTvReadyThenSendsTheKey()
    {
        var result = await Run("tv-home");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["wake", "hdmi1", "home"], _shown);
    }

    [Fact]
    public async Task TechnicsAlreadyConnectedIsDoneWithSonyNotNeeded()
    {
        _flags.Set("technics-default", true);

        var result = await Run("headphones");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(NodeOutcome.NotNeeded, result.Nodes.Single(n => n.NodeId == "sony").Outcome);
        Assert.Equal([NodeOutcome.Succeeded, NodeOutcome.NotNeeded, NodeOutcome.NotNeeded],
            result.Nodes.Single(n => n.NodeId == "technics").Called!.Nodes.Select(n => n.Outcome));
    }

    [Fact]
    public async Task TechnicsOffRunsSonyInsteadAndHistoryKeepsBothRuns()
    {
        var result = await Run("headphones");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        var technics = result.Nodes.Single(n => n.NodeId == "technics");
        Assert.Equal((NodeOutcome.Failed, FlowOutcome.Failed), (technics.Outcome, technics.Called!.Outcome));
        Assert.StartsWith("Make Technics the default didn't finish:", technics.Error, StringComparison.Ordinal);
        Assert.Equal(["sony"], _shown);

        // One record for the top run; the nested results survive the history file (a reload reads them back).
        var record = Assert.Single(new RunHistory(Path.Combine(_dir, "history.jsonl")).Recent);
        Assert.Equal(("headphones", 2), (record.FlowId, record.Nodes.Length));
        Assert.Equal("make-sony", record.Nodes.Single(n => n.NodeId == "sony").Called!.FlowId);
    }

    [Fact]
    public async Task StartModeLaunchesTheFlowAsItsOwnRun()
    {
        var finished = new TaskCompletionSource<RunFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
        _launcher.RunCompleted += f =>
        {
            if (f.FlowId == "tv-home")
                finished.TrySetResult(f);
        };

        var result = await Run("kick-off");
        var launched = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(FlowOutcome.Succeeded, launched.Outcome);
        var record = _history.Recent.Single(r => r.FlowId == "tv-home");
        Assert.Equal("Flow · Kick off", record.Trigger);
    }

    private sealed class Recorder(List<string> shown) : INotifier
    {
        public void Show(string flowName, string text, bool sound)
        {
            lock (shown)
                shown.Add(text);
        }
    }
}
