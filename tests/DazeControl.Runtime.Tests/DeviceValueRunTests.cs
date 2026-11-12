using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;

namespace DazeControl.Runtime.Tests;

/// <summary>
/// D-130 (T23.6): <c>$result:id.value</c> fills a device setting just before the step, so its lock is the filled device's
/// (fakes only: no audio or Bluetooth changes).
/// </summary>
public sealed class DeviceValueRunTests : IDisposable
{
    private const string Library = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
          bluetooth:
            buds: { address: "B8:20:8E:ED:48:FC", name: Buds }
          bound:
            pair: { bluetooth: buds, audio: sony, name: Pair }

        """;

    private readonly ManualTime _time = new();
    private readonly List<string> _defaults = [];

    /// <summary>What the fake <c>audio.setDefault</c> found as the default before it ran; null = gives no <c>previous</c>.</summary>
    private string? _before = "sony";

    public void Dispose() => _time.Dispose();

    private FlowRunner Runner(params IStepProvider[] more) => new([
        new PromptRunTests.Recorder("notify", _ => { }),
        new PromptRunTests.Recorder("audio.setDefault", c =>
        {
            var device = ((ScalarParam)c.Node.With["device"]).Text;
            lock (_defaults)
                _defaults.Add(device);
            c.SetDevice("device", device);
            if (_before is { } before && c.Wants("previous"))
                c.SetDevice("previous", before);
            _before = device;
        }),
        new PromptRunTests.Recorder("bluetooth.connect", c => c.SetDevice("device", ((ScalarParam)c.Node.With["device"]).Text)),
        .. more,
    ], _time);

    private static Flow Load(string nodes)
    {
        var load = ConfigYaml.Load(Library + "flows:\n  f:\n    nodes:\n" + nodes);
        Assert.Empty(load.Problems);
        Assert.DoesNotContain(ConfigValidator.Validate(load.Config!), p => p.Severity == Severity.Error);
        return load.Config!.Flows[0];
    }

    private static Library Lib => ConfigYaml.Load(Library + "flows: {}\n").Config!.Library;

    private const string SwitchBack = """
              - { id: set, step: audio.setDefault, with: { device: technics } }
              - { id: note, step: notify, with: { text: Hi }, after: [set] }
              - { id: back, step: audio.setDefault, with: { device: "$result:set.previous" }, after: [note] }

        """;

    private static NodeResult Node(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id);

    [Fact]
    public async Task ALaterStepPutsBackThePreviousDefault()
    {
        var result = await Bound(Runner().RunAsync(Load(SwitchBack), Lib, [], TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["technics", "sony"], _defaults);
    }

    [Fact]
    public async Task NoPreviousDefaultFailsTheStepAndChangesNothing()
    {
        _before = null; // the old default wasn't in the Library
        var result = await Bound(Runner().RunAsync(Load(SwitchBack), Lib, [], TestContext.Current.CancellationToken));

        Assert.Equal(NodeOutcome.Failed, Node(result, "back").Outcome);
        Assert.Equal("\"Set default device\" gave no Previous default.", Node(result, "back").Error);
        Assert.Equal(["technics"], _defaults); // never a silent fallback to any device
    }

    [Fact]
    public async Task OnlyWantedValuesAreAskedFor()
    {
        // Nothing uses "previous": the provider is told not to read it.
        var result = await Bound(Runner().RunAsync(Load("""
                  - { id: set, step: audio.setDefault, with: { device: technics } }
                  - { id: back, step: audio.setDefault, with: { device: "$result:set.device" }, after: [set] }

            """), Lib, [], TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["technics", "technics"], _defaults);
    }

    [Fact]
    public async Task APickResultStillFillsAsBefore()
    {
        var runner = new FlowRunner([new PickProvider(new PromptRunTests.FakePrompt { Script = _ => new("sony", Runtime.Menus.MenuCloseReason.Picked) }),
            new PromptRunTests.Recorder("audio.setDefault", c => _defaults.Add(((ScalarParam)c.Node.With["device"]).Text))], _time);
        var result = await Bound(runner.RunAsync(Load("""
                  - { id: pick, step: prompt.pick, with: { kind: audio } }
                  - { id: use, step: audio.setDefault, with: { device: "$result:pick" }, after: [pick] }
                  - { id: also, step: audio.setDefault, with: { device: "$result:pick.device" }, after: [pick] }

            """), Lib, [], TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["sony", "sony"], _defaults);
    }

    [Fact]
    public async Task AFilledDeviceSharesTheLockOfTheSameDeviceWrittenAsAKey()
    {
        // "pair" (a bound device) and "buds" are one Bluetooth device: the parallel disconnects must not overlap.
        var disconnect = new Overlap("bluetooth.disconnect");
        var result = await Bound(Runner(disconnect).RunAsync(Load("""
                  - { id: bt, step: bluetooth.connect, with: { device: pair } }
                  - { id: a, step: bluetooth.disconnect, with: { device: buds }, after: [bt] }
                  - { id: b, step: bluetooth.disconnect, with: { device: "$result:bt.device" }, after: [bt] }

            """), Lib, [], TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["bluetooth:B8:20:8E:ED:48:FC", "bluetooth:B8:20:8E:ED:48:FC"], disconnect.Locks);
        Assert.Contains("pair", disconnect.Devices); // the provider saw the filled key, never "$result:…"
        Assert.Equal(1, disconnect.MostAtOnce);
    }

    [Fact]
    public async Task TheDryRunSaysItWouldUseTheValue()
    {
        var dry = EngineController.CreateDryRunner(_time);
        var run = await Bound(dry.RunAsync(Load(SwitchBack), Lib, TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, run.Outcome);
        Assert.Equal("Would use Previous default from Set default device", Node(run, "back").Summary);
    }

    /// <summary>Records each run's device and lock, and how many ran at once (each holds for a short real delay).</summary>
    private sealed class Overlap(string id) : IStepProvider
    {
        private int _running;

        public string Id => id;

        public List<string> Locks { get; } = [];

        public List<string> Devices { get; } = [];

        public int MostAtOnce { get; private set; }

        public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _running);
            lock (Locks)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
                Locks.Add(Assert.Single(FlowRunner.ResourcesOf(context.Descriptor, context.Node, context.Library)));
                Devices.Add(((ScalarParam)context.Node.With["device"]).Text);
            }
            await Task.Delay(100, CancellationToken.None);
            Interlocked.Decrement(ref _running);
        }
    }
}
