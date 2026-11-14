using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Runtime.Control;
using CtrlAltDaze.Runtime.Providers;
using static CtrlAltDaze.Runtime.Tests.FlowRunnerTests;
using static CtrlAltDaze.Runtime.Tests.PromptRunTests;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>T23.7 at run time (DESIGN §9): text inputs, flow outputs across <c>flow.run</c>, Make text. Fake providers.</summary>
public sealed class TextValueRunTests : IDisposable
{
    private const string Yaml = """
        library:
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC", name: Technics }
            odd: { address: "B8:20:8E:ED:48:FD", name: "{bt.device}" }
        flows:
          inner:
            name: Connect headphones
            inputs:
              greeting: { kind: text, name: Greeting, default: Hi }
            outputs:
              device: "{bt.device}"
              said: "{$greeting} from {@flow}"
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }
          caller:
            name: Evening
            nodes:
              - { id: first, step: bluetooth.connect, with: { device: technics } }
              - { id: call, step: flow.run, with: { flow: inner, inputs: { greeting: "Hello {first.device}" } }, after: [first] }
              - { id: note, step: notify, with: { text: "{call.device} / {call.said}" }, after: [call] }
          defaults:
            name: Defaults
            nodes:
              - { id: call, step: flow.run, with: { flow: inner } }
              - { id: note, step: notify, with: { text: "{call.said}" }, after: [call] }
          middle:
            name: Middle
            inputs:
              greeting: { kind: text, name: Greeting, default: Hey }
            outputs:
              dev: "{c.device}"
              said: "{c.said}"
            nodes:
              - { id: c, step: flow.run, with: { flow: inner, inputs: { greeting: $greeting } } }
          top:
            name: Top
            nodes:
              - { id: first, step: bluetooth.connect, with: { device: odd } }
              - { id: m, step: flow.run, with: { flow: middle, inputs: { greeting: "x {first.device}" } }, after: [first] }
              - { id: note, step: notify, with: { text: "{m.dev} / {m.said}" }, after: [m] }
          made:
            name: Made
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }
              - { id: line, step: text.make, with: { text: "{bt.device} at home" }, after: [bt] }
              - { id: a, step: notify, with: { text: "{line.text}!" }, after: [line] }
              - { id: b, step: notify, with: { text: "Again: {line.text}" }, after: [line] }
          gone:
            name: Gone
            nodes:
              - { id: call, step: flow.run, with: { flow: inner } }
              - { id: note, step: notify, with: { text: "[{call.gone|none}] [{call.gone}] [{call.device}]" }, after: [call] }
          started:
            name: Started
            nodes:
              - { id: call, step: flow.run, with: { flow: inner, wait: false } }
        """;

    private readonly ManualTime _time = new();
    private readonly List<string> _said = [];
    private readonly Configuration _config;
    private readonly Starter _starter;
    private readonly FlowRunner _runner;

    public TextValueRunTests()
    {
        var load = ConfigYaml.Load(Yaml);
        Assert.Empty(load.Problems);
        _config = load.Config!;
        var problems = ConfigValidator.Validate(_config);
        Assert.DoesNotContain(problems, p => p.Severity == Severity.Error);
        // A call's output the called flow doesn't give is a warning only (T23.7): the run shows the fallback.
        Assert.Contains(problems, p => p is { Severity: Severity.Warning, FlowId: "gone" } && p.Message.Contains("\"gone\"", StringComparison.Ordinal));
        _starter = new Starter(_config);
        _runner = new FlowRunner(
        [
            new Recorder("bluetooth.connect", c => c.SetDevice("device", ((ScalarParam)c.Node.With["device"]).Text)),
            new Recorder("notify", c => { lock (_said) _said.Add(((ScalarParam)c.Node.With["text"]).Text); }),
            new Recorder("text.make", c => c.SetValue("text", ((ScalarParam)c.Node.With["text"]).Text)),
            new FlowProvider(_starter),
        ], _time) { Flows = _starter };
    }

    public void Dispose() => _time.Dispose();

    private Task<RunResult> Run(string flowId) =>
        Bound(_runner.RunAsync(_config.Flows.Single(f => f.Id == flowId), _config.Library, TestContext.Current.CancellationToken));

    [Fact]
    public async Task ACallGivesTheCalledFlowsOutputsAndPassesTextFilledInTheCaller()
    {
        var result = await Run("caller");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["Technics / Hello Technics from Connect headphones"], _said);
    }

    [Fact]
    public async Task ATextInputNobodyPassesIsItsDefault()
    {
        await Run("defaults");

        Assert.Equal(["Hi from Connect headphones"], _said);
    }

    [Fact]
    public async Task OutputsPassThroughNestedCallsAndPassedTextIsNeverReadAgain()
    {
        // The device's name looks like a reference: the middle flow passes it on as text, so the inner flow shows it as written.
        var result = await Run("top");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["Technics / x {bt.device} from Connect headphones"], _said);
    }

    [Fact]
    public async Task MakeTextIsSetOnceAndReadByTwoSteps()
    {
        await Run("made");

        Assert.Equal(["Again: Technics at home", "Technics at home!"], _said.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnOutputTheCalledFlowDoesntGiveShowsItsFallbackElseNothing()
    {
        await Run("gone");

        Assert.Equal(["[none] [] [Technics]"], _said);
    }

    [Fact]
    public async Task ACalledFlowThatFailsGivesNoOutputs()
    {
        var failing = _config.Flows.Single(f => f.Id == "inner") with
        {
            Nodes = [new StepNode("bt", "bluetooth.connect") { With = new([KeyValuePair.Create("device", (ParamValue)"technics")]) }, new StepNode("boom", "boom")],
        };
        _starter.Override = failing;
        var flow = _config.Flows.Single(f => f.Id == "gone") with
        {
            // "Run the next steps anyway": the message runs after the failed call.
            Nodes = [.. _config.Flows.Single(f => f.Id == "gone").Nodes.Select(n => n is StepNode { Id: "call" } s
                ? s with { OnFailure = FailurePolicy.Ignore } : n)],
        };

        var result = await Bound(_runner.RunAsync(flow, _config.Library, TestContext.Current.CancellationToken));

        Assert.Equal(FailurePolicy.Ignore, ((StepNode)flow.FindNode("call")!).OnFailure);
        Assert.Equal(NodeOutcome.FailedIgnored, StepResultOf(result, "call"));
        Assert.Equal(["[none] [] []"], _said);
    }

    [Fact]
    public async Task ARunsResultCarriesItsFilledOutputs()
    {
        var result = await Bound(_runner.RunNestedAsync(_config.Flows.Single(f => f.Id == "inner"), _config.Library, [], Guid.NewGuid(), "x",
            new Dictionary<string, string> { ["greeting"] = "Yo" }, TestContext.Current.CancellationToken));

        Assert.Equal("Technics", result.Outputs["device"]);
        Assert.Equal("Yo from Connect headphones", result.Outputs["said"]);
    }

    [Fact]
    public async Task AnOutputFilledFromASensitiveInputStaysSensitive()
    {
        // The caller passed a Sensitive value as text: the called run's output holding it is marked, and the caller hides it.
        var values = new Dictionary<string, string> { ["greeting"] = "s3cret" };
        var flow = _config.Flows.Single(f => f.Id == "inner");
        var runner = new FlowRunner([new Recorder("bluetooth.connect", c => c.SetDevice("device", "technics"))], _time) { Flows = _starter };
        var sensitive = new HashSet<string>(StringComparer.Ordinal) { "greeting" };
        var result = await Bound(runner.RunNestedAsync(flow, _config.Library, [], Guid.NewGuid(), "x", values, TestContext.Current.CancellationToken));

        Assert.Empty(result.SensitiveOutputs); // not filled from a Sensitive value here
        var run = new CtrlAltDaze.Core.Runs.RunValues();
        run.SetText("call", "said", "s3cret from x");
        run.MarkSensitive("call", "said");
        var caller = _config.Flows.Single(f => f.Id == "caller");
        var secrets = CtrlAltDaze.Core.Model.StepValues.Secrets(caller, "note", run, _config.Library, flows: _starter.Lookup);
        Assert.Equal("Failed: ‹Said›", CtrlAltDaze.Core.Model.StepValues.Mask("Failed: s3cret from x", secrets));
    }

    [Fact]
    public async Task ACallThatDoesntWaitGivesNoOutputs()
    {
        var result = await Run("started");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Single(_starter.Started);
        Assert.Empty(result.Outputs);
    }

    private static NodeOutcome StepResultOf(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id).Outcome;

    private sealed class Starter(Configuration config) : IFlowStarter
    {
        public Flow? Override { get; set; }

        public List<string> Started { get; } = [];

        public CalledFlow Resolve(string flowId) =>
            (Override is { } o && o.Id == flowId ? o : config.Flows.FirstOrDefault(f => f.Id == flowId)) is { } flow
                ? new(flow, config.Library, flow.Name, null)
                : new(null, Library.Empty, flowId, $"{flowId} isn't there anymore.");

        public Flow? Lookup(string id) => Resolve(id).Flow;

        public bool IsRunning(string flowId) => false;

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName)
        {
            lock (Started)
                Started.Add(flowId);
            return new(true);
        }
    }
}
