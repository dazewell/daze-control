using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;

namespace DazeControl.Runtime.Tests;

/// <summary>Flow inputs at run time (D-064, flow-calls/DESIGN.md §1 B and §5), with fake audio and Bluetooth steps.</summary>
public sealed class FlowInputRunTests : IDisposable
{
    private const string Yaml = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC" }
            sony: { address: "AC:80:0A:11:22:33" }
          bound:
            technics-pair: { bluetooth: technics, audio: technics }
            sony-pair: { bluetooth: sony, audio: sony }
        flows:
          bound-in:
            name: Bound input
            inputs:
              headphones: { kind: bound, name: Headphones, default: technics-pair }
            nodes:
              - { id: connect, step: bluetooth.connect, with: { device: $headphones } }
              - { id: check, step: bluetooth.check, with: { device: $headphones }, after: [connect] }
              - { id: setDefault, step: audio.setDefault, with: { device: $headphones }, after: [check] }
          bound-call:
            name: Bound call
            nodes:
              - { id: call, step: flow.run, with: { flow: bound-in, inputs: { headphones: sony-pair } } }
          make-default:
            name: Make it the default
            tray: { show: false }
            inputs:
              output:    { kind: audio,     name: Output,           default: technics }
              bluetooth: { kind: bluetooth, name: Bluetooth device, default: technics }
            nodes:
              - { id: set,     step: audio.setDefault,  with: { device: $output }, onFailure: skip }
              - { id: connect, step: bluetooth.connect, with: { device: $bluetooth }, after: [{ id: set, when: failure }] }
              - { id: again,   step: audio.setDefault,  with: { device: $output }, after: [connect] }
          headphones:
            name: Headphones
            nodes:
              - id: technics
                step: flow.run
                with: { flow: make-default, inputs: { output: technics, bluetooth: technics } }
                onFailure: skip
              - id: sony
                step: flow.run
                with: { flow: make-default, inputs: { output: sony, bluetooth: sony } }
                after: [{ id: technics, when: failure }]
          sony-only:
            name: Sony only
            inputs:
              out: { kind: audio, name: Output, default: sony }
            nodes:
              - { id: call, step: flow.run, with: { flow: make-default, inputs: { output: $out, bluetooth: sony } } }
        """;

    private readonly ManualTime _time = new();
    private readonly Configuration _config;
    private readonly Devices _devices = new();
    private readonly Starter _starter;
    private readonly FlowRunner _runner;

    public FlowInputRunTests()
    {
        var load = ConfigYaml.Load(Yaml);
        Assert.Empty(load.Problems);
        _config = load.Config!;
        _starter = new Starter(_config);
        _runner = new FlowRunner([new Step(_devices, "audio.setDefault"), new Step(_devices, "bluetooth.connect"), new Step(_devices, "bluetooth.check"), new FlowProvider(_starter)], _time)
        {
            Flows = _starter,
        };
    }

    public void Dispose() => _time.Dispose();

    private Task<RunResult> Run(string flowId) =>
        Bound(_runner.RunAsync(_config.Flows.Single(f => f.Id == flowId), _config.Library, TestContext.Current.CancellationToken));

    private static NodeResult NodeOf(IEnumerable<NodeResult> nodes, string id) => nodes.Single(n => n.NodeId == id);

    [Fact]
    public async Task TechnicsOffRunsTheChainForTechnicsThenForSony()
    {
        _devices.Broken.UnionWith(["audio.setDefault:technics", "bluetooth.connect:technics"]);

        var result = await Run("headphones");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            [
                ("audio.setDefault", "technics", "audio"),
                ("bluetooth.connect", "technics", "bluetooth:B8:20:8E:ED:48:FC"),
                ("audio.setDefault", "sony", "audio"),
            ],
            _devices.Calls);
        Assert.Equal(NodeOutcome.Failed, NodeOf(result.Nodes, "technics").Outcome);
        var sony = NodeOf(result.Nodes, "sony");
        Assert.Equal(NodeOutcome.Succeeded, sony.Outcome);
        Assert.Equal(NodeOutcome.Succeeded, NodeOf(sony.Called!.Nodes, "set").Outcome);
    }

    /// <summary>D-125: a bound input reaches each step as the bound key; Bluetooth steps lock its Bluetooth side.</summary>
    [Fact]
    public async Task ABoundInputRunsEachStepOnItsSide()
    {
        var result = await Run("bound-in");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            [
                ("bluetooth.connect", "technics-pair", "bluetooth:B8:20:8E:ED:48:FC"),
                ("bluetooth.check", "technics-pair", "bluetooth:B8:20:8E:ED:48:FC"),
                ("audio.setDefault", "technics-pair", "audio"),
            ],
            _devices.Calls);
        Assert.Equal("B8:20:8E:ED:48:FC", _config.Library.ResolveBluetooth("technics-pair")!.Address);
        Assert.Equal("Technics", _config.Library.ResolveAudio("technics-pair")!.Match);
    }

    [Fact]
    public async Task ACallerPassesAnotherBoundDevice()
    {
        var result = await Run("bound-call");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.All(_devices.Calls, c => Assert.Equal("sony-pair", c.Device));
        Assert.Contains(("bluetooth.connect", "sony-pair", "bluetooth:AC:80:0A:11:22:33"), _devices.Calls);
    }

    [Fact]
    public async Task TechnicsReadyMakesSonyNotNeeded()
    {
        var result = await Run("headphones");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal([("audio.setDefault", "technics", "audio")], _devices.Calls);
        Assert.Equal(NodeOutcome.NotNeeded, NodeOf(result.Nodes, "sony").Outcome);
    }

    [Fact]
    public async Task EachDeviceTakesItsOwnBluetoothLock()
    {
        _devices.Broken.UnionWith(["audio.setDefault:technics", "audio.setDefault:sony", "bluetooth.connect:technics"]);

        await Run("headphones");

        Assert.Equal(["bluetooth:B8:20:8E:ED:48:FC", "bluetooth:AC:80:0A:11:22:33"],
            _devices.Calls.Where(c => c.Step == "bluetooth.connect").Select(c => c.Lock));
    }

    [Fact]
    public async Task ARunOnItsOwnUsesTheDefaults()
    {
        var result = await Run("make-default");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal([("audio.setDefault", "technics", "audio")], _devices.Calls);
    }

    [Fact]
    public async Task AnInputPassedThroughGetsTheCallersValue()
    {
        await Run("sony-only");

        Assert.Equal([("audio.setDefault", "sony", "audio")], _devices.Calls);
    }

    [Fact]
    public async Task APassedDeviceMissingFromTheCalledFlowsLibraryFailsTheCall()
    {
        _starter.Library = _config.Library with { Audio = [.. _config.Library.Audio.Where(d => d.Key != "sony")] };
        _devices.Broken.Add("audio.setDefault:technics");
        _devices.Broken.Add("bluetooth.connect:technics");

        var result = await Run("headphones");

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        var sony = NodeOf(result.Nodes, "sony");
        Assert.Equal("Headphones passes Output \"sony\", but there's no audio device called that in your Library.", sony.Error);
        Assert.Null(sony.Called);
    }

    [Fact]
    public async Task StartModeStartsTheFlowWithTheGivenInputs()
    {
        var flow = _config.Flows.Single(f => f.Id == "headphones");
        var start = flow with
        {
            Nodes = [new StepNode("go", ProviderCatalog.RunFlowId)
            {
                With = new([
                    KeyValuePair.Create("flow", (ParamValue)"make-default"),
                    KeyValuePair.Create("wait", (ParamValue)"false"),
                    KeyValuePair.Create("inputs", (ParamValue)new MapParam(new([KeyValuePair.Create("output", (ParamValue)"sony")]))),
                ]),
            }],
        };

        var result = await Bound(_runner.RunAsync(start, _config.Library, TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        var (flowId, inputs) = Assert.Single(_starter.Started);
        Assert.Equal("make-default", flowId);
        Assert.Equal(new Dictionary<string, string> { ["output"] = "sony" }, inputs);
    }

    /// <summary>Records each device step with the device it got and the lock it would take; fails the broken ones.</summary>
    private sealed class Devices
    {
        public HashSet<string> Broken { get; } = [];

        public List<(string Step, string Device, string Lock)> Calls { get; } = [];
    }

    private sealed class Step(Devices devices, string id) : IStepProvider
    {
        public string Id => id;

        public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            var device = ((ScalarParam)context.Node.With["device"]).Text;
            lock (devices)
                devices.Calls.Add((id, device, Assert.Single(FlowRunner.ResourcesOf(context.Descriptor, context.Node, context.Library))));
            return devices.Broken.Contains($"{id}:{device}") ? FakeProvider.Fail($"{device} didn't answer.") : Task.CompletedTask;
        }
    }

    private sealed class Starter(Configuration config) : IFlowStarter
    {
        public Library Library { get; set; } = config.Library;

        public List<(string FlowId, IReadOnlyDictionary<string, string> Inputs)> Started { get; } = [];

        public CalledFlow Resolve(string flowId) =>
            config.Flows.FirstOrDefault(f => f.Id == flowId) is { } flow
                ? new(flow, Library, flow.Name, null)
                : new(null, Library.Empty, flowId, $"{flowId} isn't there anymore. Pick another flow.");

        public bool IsRunning(string flowId) => false;

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName)
        {
            lock (Started)
                Started.Add((flowId, inputs));
            return new(true);
        }
    }
}
