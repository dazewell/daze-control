using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Menus;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;

namespace DazeControl.Runtime.Tests;

/// <summary><c>prompt.pick</c> and <c>$result:id</c> at run time (D-117), with a fake presenter: no window.</summary>
public sealed class PickRunTests : IDisposable
{
    private const string TwoOutputs = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }

        """;

    private readonly ManualTime _time = new();
    private readonly PromptRunTests.FakePrompt _presenter = new();
    private readonly List<string> _devices = [];

    public void Dispose() => _time.Dispose();

    private FlowRunner Runner() => new([new PickProvider(_presenter), new PromptRunTests.Recorder("notify", _ => { }),
        new PromptRunTests.Recorder("audio.setDefault", c => _devices.Add(((ScalarParam)c.Node.With["device"]).Text))], _time);

    private static (Flow Flow, Library Library) Load(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        Assert.DoesNotContain(DazeControl.Core.Validation.ConfigValidator.Validate(load.Config!), p => p.Severity == DazeControl.Core.Validation.Severity.Error);
        return (load.Config!.Flows[0], load.Config.Library);
    }

    private static (Flow Flow, Library Library) PickThenUse(string with = "", string library = TwoOutputs, string onFailure = "") => Load(library + $$"""
        flows:
          f:
            nodes:
              - { id: pick, step: prompt.pick, with: { kind: audio{{with}} }{{onFailure}} }
              - { id: use, step: audio.setDefault, with: { device: "$result:pick" }, after: [{ id: pick, when: always }] }

        """);

    private async Task<RunResult> Run((Flow Flow, Library Library) loaded, string trigger = "Tray") =>
        await Bound(Runner().RunAsync(loaded.Flow, loaded.Library, [], TestContext.Current.CancellationToken, trigger));

    private static NodeResult Node(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id);

    [Fact]
    public async Task TheLaterStepGetsThePickedDevice()
    {
        _presenter.Script = _ => new("sony", MenuCloseReason.Picked);
        var result = await Run(PickThenUse());

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal("Picked sony", Node(result, "pick").Summary);
        Assert.Equal(["sony"], _devices);
        var shown = Assert.Single(_presenter.Shown);
        Assert.Equal("Pick an audio device", shown.Title);
        Assert.Equal(["technics", "sony"], shown.Items.Select(i => i.FlowId));
    }

    [Fact]
    public async Task NoAnswerUsesTheDefaultWhenToldTo()
    {
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        var result = await Run(PickThenUse(", onTimeout: use-default, default: technics, question: Which speakers?"));

        Assert.Equal(["technics"], _devices);
        Assert.Equal("Which speakers?", _presenter.Shown[0].Title);
        Assert.Equal("technics", _presenter.Shown[0].LastPickedFlowId);
        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
    }

    [Theory]
    [InlineData("bt1")] // the key
    [InlineData("Technics")] // a name typed by hand in an older flow (D-128)
    public async Task NoAnswerUsesTheDefaultByKeyOrByLegacyName(string fallback)
    {
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        var loaded = Load($$"""
            library:
              bluetooth:
                bt1: { address: "B8:20:8E:ED:48:FC", name: Technics }
                bt2: { address: "B8:20:8E:ED:48:FD", name: Sony }
            flows:
              f:
                nodes:
                  - { id: pick, step: prompt.pick, with: { kind: bluetooth, onTimeout: use-default, default: {{fallback}} } }
            """);
        var result = await Run(loaded);

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal("Picked Technics", Node(result, "pick").Summary);
        Assert.Equal("bt1", _presenter.Shown[0].LastPickedFlowId);
    }

    [Fact]
    public async Task NoAnswerStopsTheRunQuietlyByDefault()
    {
        _presenter.Script = _ => new(null, MenuCloseReason.TimedOut);
        var result = await Run(PickThenUse());

        Assert.True(result.Quiet);
        Assert.Equal("No answer", Node(result, "pick").Error);
        Assert.Empty(_devices);
    }

    [Fact]
    public async Task OneItemIsPickedWithoutAsking()
    {
        var result = await Run(PickThenUse(library: """
            library:
              audio:
                technics: { match: Technics }

            """));

        Assert.Empty(_presenter.Shown);
        Assert.Equal(["technics"], _devices);
        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task AStepWhosePickFailedSaysNothingWasPicked()
    {
        var result = await Run(PickThenUse(library: "library: {}\n", onFailure: ", onFailure: ignore"));

        Assert.Equal("Your Library has no audio device to pick.", Node(result, "pick").Error);
        Assert.Equal(NodeOutcome.Failed, Node(result, "use").Outcome);
        Assert.Equal("Nothing was picked in \"Pick device\".", Node(result, "use").Error);
        Assert.Empty(_devices);
    }

    [Fact]
    public async Task TheDryRunAssumesTheDefaultAndTheLaterStepChecksIt()
    {
        var dry = EngineController.CreateDryRunner(_time);
        var (flow, library) = PickThenUse(", default: sony");

        var run = await Bound(dry.RunAsync(flow, library, TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, run.Outcome);
        Assert.Equal("Would ask \"Pick an audio device\", assuming sony", Node(run, "pick").Summary);
        Assert.Empty(_presenter.Shown);
    }

    [Fact]
    public async Task StartedAfterThePickTheDeviceStepFailsClearly()
    {
        // T27.4, D-149: the Pick counts as done but gave nothing; a device setting never falls back (D-117).
        var loaded = PickThenUse();
        var result = await Bound(Runner().RunAsync(loaded.Flow, loaded.Library, [], TestContext.Current.CancellationToken, "Editor", startAt: "use"));

        Assert.Empty(_presenter.Shown);
        Assert.Empty(_devices);
        Assert.Equal(NodeOutcome.Succeeded, Node(result, "pick").Outcome);
        Assert.Equal(0, Node(result, "pick").Attempts);
        Assert.Equal("this run started at Set default device", Node(result, "pick").SkippedBecause);
        Assert.Equal(NodeOutcome.Failed, Node(result, "use").Outcome);
        Assert.Equal("\"Pick device\" didn't run: this run started after it.", Node(result, "use").Error);
        Assert.Equal(FlowOutcome.Failed, result.Outcome);
    }
}
