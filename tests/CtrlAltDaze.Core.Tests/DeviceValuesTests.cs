using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>D-130 (T23.6): any step's Device value fills a device setting as <c>$result:id.value</c>; <c>audio.setDefault</c> gives <c>previous</c>.</summary>
public class DeviceValuesTests
{
    private const string Header = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
          bluetooth:
            buds: { address: "B8:20:8E:ED:48:FC", name: Buds }
        flows:
          f:
            name: F
            nodes:

        """;

    private static Configuration Load(string nodes)
    {
        // The nodes are written at list level ("  - "); the flow's list sits four spaces deeper.
        var load = ConfigYaml.Load(Header + string.Join('\n', nodes.Split('\n').Select(l => l.Length > 0 ? "    " + l : l)));
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private static List<Problem> Problems(string nodes) => [.. ConfigValidator.Validate(Load(nodes))];

    private const string SwitchBack = """
              - { id: set, step: audio.setDefault, with: { device: technics } }
              - { id: note, step: notify, with: { text: Hi }, after: [set] }
              - { id: back, step: audio.setDefault, with: { device: "$result:set.previous" }, after: [note] }

        """;

    [Theory]
    [InlineData("$result:pick", "pick", "device")]
    [InlineData(" $result:set.previous ", "set", "previous")]
    [InlineData("$result:bt.device", "bt", "device")]
    public void ReferencesNameAStepAndAValue(string text, string step, string value)
    {
        Assert.Equal((step, value), PickResults.RefIn(text));
        Assert.Equal(step, PickResults.StepIn(text));
    }

    [Theory]
    [InlineData("$output")]
    [InlineData("$result:")]
    [InlineData("technics")]
    public void OtherTextIsNoReference(string text) => Assert.Null(PickResults.RefIn(text));

    [Fact]
    public void RefWritesTheShortFormForDevice()
    {
        Assert.Equal("$result:pick", PickResults.Ref("pick", "device"));
        Assert.Equal("$result:set.previous", PickResults.Ref("set", "previous"));
    }

    [Fact]
    public void SetDefaultGivesThePreviousDefault()
    {
        var values = StepValues.Of(ProviderCatalog.Find("audio.setDefault")!);
        var previous = Assert.Single(values, v => v.Name == "previous");
        Assert.Equal((ValueKind.Device, LibraryKind.Audio, "Previous default"), (previous.Kind, previous.Library, previous.Title));
    }

    [Fact]
    public void ASwitchBackFlowIsValid() => Assert.Empty(Problems(SwitchBack));

    [Fact]
    public void AStepThatCantHaveRunIsAnError()
    {
        var problem = Assert.Single(Problems("""
              - { id: set, step: audio.setDefault, with: { device: technics } }
              - { id: back, step: audio.setDefault, with: { device: "$result:set.previous" } }

            """));
        Assert.Equal((Severity.Error, "flows.f.nodes.back.with.device"), (problem.Severity, problem.Path));
        Assert.Equal("\"Set default device\" must run before this step: connect it so this step comes after.", problem.Message);
    }

    [Fact]
    public void AnAudioValueCantFillABluetoothSetting()
    {
        var problem = Assert.Single(Problems("""
              - { id: set, step: audio.setDefault, with: { device: technics } }
              - { id: bt, step: bluetooth.connect, with: { device: "$result:set.previous" }, after: [set] }

            """));
        Assert.Equal("Previous default of \"Set default device\" is an audio device; this setting needs a Bluetooth device.", problem.Message);
    }

    [Theory]
    [InlineData("$result:set.nmae", "Set default device gives no value called \"nmae\" (it gives Device, Previous default, Why it failed).")]
    [InlineData("$result:set.error", "Why it failed of \"Set default device\" isn't a device; this setting needs an audio device.")]
    [InlineData("$result:gone.previous", "There's no step called \"gone\" in this flow.")]
    [InlineData("$result:gone", "There's no Pick step called \"gone\" in this flow.")] // D-117's sentence stays
    public void BrokenReferencesAreErrors(string text, string message)
    {
        var problem = Assert.Single(Problems($$"""
              - { id: set, step: audio.setDefault, with: { device: technics } }
              - { id: back, step: audio.setDefault, with: { device: "{{text}}" }, after: [set] }

            """));
        Assert.Equal((Severity.Error, message), (problem.Severity, problem.Message));
    }

    [Fact]
    public void AValueThatMayBeMissingWarnsThatTheStepFails()
    {
        var problem = Assert.Single(Problems("""
              - { id: set, step: audio.setDefault, with: { device: technics }, only: notDefault }
              - { id: back, step: audio.setDefault, with: { device: "$result:set.previous" }, after: [set] }

            """));
        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Equal("\"Set default device\" may be skipped (Only if), so Previous default can be empty. This step fails when it has none.",
            problem.Message);
    }

    [Fact]
    public void FlowRunPassesAnotherStepsDevice()
    {
        var problems = ConfigValidator.Validate(ConfigYaml.Load(Header + """
                  - { id: set, step: audio.setDefault, with: { device: technics } }
                  - { id: call, step: flow.run, with: { flow: g, inputs: { output: "$result:set.previous" } }, after: [set] }
              g:
                name: G
                inputs:
                  output: { kind: audio, name: Output, default: sony }
                nodes:
                  - { id: s, step: audio.setDefault, with: { device: $output } }

            """).Config!);
        Assert.DoesNotContain(problems, p => p.Severity == Severity.Error);
    }

    [Fact]
    public void WantedKeepsTheValueAndFillSwapsItsKey()
    {
        var flow = Load(SwitchBack).Flows[0];
        Assert.Contains(("set", "previous"), StepValues.Wanted(flow));

        var values = new RunValues(StepValues.Wanted(flow));
        var back = (StepNode)flow.FindNode("back")!;
        StepValues.Fill(flow, back, values, Library.Empty, out var missing);
        Assert.Equal("set.previous", missing);

        values.SetDevice("set", "previous", "sony", LibraryKind.Audio);
        var filled = StepValues.Fill(flow, back, values, Library.Empty, out missing);
        Assert.Null(missing);
        Assert.Equal(new ScalarParam("sony"), filled.With["device"]);
    }

    [Fact]
    public void DevicesBeforeListsTheValuesThatFit()
    {
        var flow = Load("""
              - { id: pick, step: prompt.pick, with: { kind: audio } }
              - { id: set, step: audio.setDefault, with: { device: technics }, after: [pick] }
              - { id: bt, step: bluetooth.connect, with: { device: buds }, after: [set] }
              - { id: x, step: audio.setVolume, with: { percent: 20 }, after: [bt] }
              - { id: y, step: bluetooth.disconnect, with: { device: buds }, after: [bt] }

            """).Flows[0];
        Assert.Equal([("pick", "device"), ("set", "device"), ("set", "previous")],
            StepValues.DevicesBefore(flow, "x", LibraryKind.Audio).Select(e => (e.Step.Id, e.Value.Name)));
        Assert.Equal([("bt", "device")], StepValues.DevicesBefore(flow, "y", LibraryKind.Bluetooth).Select(e => (e.Step.Id, e.Value.Name)));
        Assert.Empty(StepValues.DevicesBefore(flow, "pick", LibraryKind.Audio));
    }

    [Fact]
    public void DescribeNamesTheValueAndItsStep()
    {
        var flow = Load("""
              - { id: pick, name: Pick speakers, step: prompt.pick, with: { kind: audio } }
              - { id: set, step: audio.setDefault, with: { device: technics } }

            """).Flows[0];
        Assert.Equal("Result of Pick speakers", PickResults.Describe(flow, "$result:pick"));
        Assert.Equal("Previous default from Set default device", PickResults.Describe(flow, "$result:set.previous"));
        Assert.Null(PickResults.Describe(flow, "$result:gone.previous"));
    }
}
