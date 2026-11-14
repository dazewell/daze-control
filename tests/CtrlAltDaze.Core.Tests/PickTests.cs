using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>Pick steps (<c>prompt.pick</c>, D-117) and <c>$result:id</c> in YAML and validation.</summary>
public class PickTests
{
    private const string Library = """
        library:
          audio:
            technics: { match: Technics }
          bluetooth:
            sony: { address: "B8:20:8E:ED:48:FC" }

        """;

    private static string Flow(string nodes) => Library + $"""
        flows:
          f:
            nodes:
        {nodes}

        """;

    private const string Pick = "      - { id: pick, step: prompt.pick, with: { kind: audio } }\n";

    private static List<Problem> Problems(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return [.. ConfigValidator.Validate(load.Config!)];
    }

    private static List<Problem> Errors(string yaml) => [.. Problems(yaml).Where(p => p.Severity == Severity.Error)];

    [Fact]
    public void AStepAfterThePickCanUseItsResultAndItRoundTrips()
    {
        var yaml = Flow(Pick + "      - { id: use, step: audio.setDefault, with: { device: \"$result:pick\" }, after: [pick] }");
        Assert.Empty(Errors(yaml));
        var load = ConfigYaml.Load(yaml);
        var again = ConfigYaml.Load(ConfigYaml.Save(load.Config!));
        var use = (StepNode)again.Config!.Flows[0].FindNode("use")!;
        Assert.Equal("pick", PickResults.StepIn(use.With["device"]));
        Assert.True(PickResults.IsUsed(again.Config.Flows[0], "pick"));
    }

    [Theory]
    [InlineData("      - { id: use, step: audio.setDefault, with: { device: \"$result:pick\" } }", "must run before this step")]
    [InlineData("      - { id: use, step: audio.setDefault, with: { device: \"$result:nope\" }, after: [pick] }", "no Pick step called \"nope\"")]
    [InlineData("      - { id: use, step: bluetooth.connect, with: { device: \"$result:pick\" }, after: [pick] }", "picks an audio device; this setting needs a Bluetooth device")]
    public void AWrongResultIsAnErrorOnThatSetting(string use, string message)
    {
        var error = Assert.Single(Errors(Flow(Pick + use)));
        Assert.Equal("flows.f.nodes.use.with.device", error.Path);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepInACardAfterThePickSeesItButNotTheOtherMembers()
    {
        var flow = ConfigYaml.Load(Flow(Pick + """
                  - { id: other, step: prompt.pick, with: { kind: audio } }
                  - { id: a, step: audio.setDefault, with: { device: "$result:pick" } }
                  - { id: card, group: [other, a], after: [pick] }
                  - { id: last, step: notify, with: { text: x }, after: [card] }
            """)).Config!.Flows[0];

        Assert.Equal(["pick"], PickResults.Before(flow, "a").Select(p => p.Id));
        Assert.Equal(["pick", "other"], PickResults.Before(flow, "last").Select(p => p.Id));
    }

    [Theory]
    [InlineData("kind: audio, onTimeout: use-default", "flows.f.nodes.pick.with.default")]
    [InlineData("kind: audio, onTimeout: skip", "flows.f.nodes.pick.with.onTimeout")]
    [InlineData("kind: printer", "flows.f.nodes.pick.with.kind")]
    [InlineData("kind: audio, timeout: 1s", "flows.f.nodes.pick.with.timeout")]
    public void ABadPickStepIsAnErrorOnItsOwnSetting(string with, string path) =>
        Assert.Contains(path, Errors(Flow($"      - {{ id: pick, step: prompt.pick, with: {{ {with} }} }}")).Select(p => p.Path));

    private const string NamedLibrary = """
        library:
          audio:
            technics: { match: Technics }
          bluetooth:
            bt1: { address: "B8:20:8E:ED:48:FC", name: Technics }
            bt2: { address: "B8:20:8E:ED:48:FD", name: Sony }
            bt3: { address: "B8:20:8E:ED:48:FE", name: Sony }
          bound:
            desk: { bluetooth: bt1, audio: technics, name: Desk }

        """;

    /// <summary>D-128: the default is a key; a hand-typed name resolves when exactly one item of that kind has it.</summary>
    [Theory]
    [InlineData(LibraryKind.Bluetooth, "bt1", "bt1")]
    [InlineData(LibraryKind.Bluetooth, "Technics", "bt1")] // a name
    [InlineData(LibraryKind.Bluetooth, "technics", "bt1")] // case ignored for names
    [InlineData(LibraryKind.Bluetooth, "Sony", null)] // two items: ambiguous
    [InlineData(LibraryKind.Bluetooth, "Desk", "desk")] // a bound device is offered too (D-116)
    [InlineData(LibraryKind.Bluetooth, "nope", null)]
    [InlineData(LibraryKind.Bluetooth, "", null)]
    [InlineData(LibraryKind.Audio, "technics", "technics")]
    [InlineData(LibraryKind.Audio, "bt1", null)] // another kind's key
    [InlineData(LibraryKind.Bound, "Desk", "desk")]
    public void ADefaultResolvesByKeyThenByName(LibraryKind kind, string text, string? expected) =>
        Assert.Equal(expected, PickResults.DefaultKey(ConfigYaml.Load(NamedLibrary).Config!.Library, kind, text));

    [Theory]
    [InlineData("kind: bluetooth, default: bt1", false)]
    [InlineData("kind: bluetooth, default: Technics", false)] // legacy name
    [InlineData("kind: bluetooth, default: Sony", true)] // ambiguous
    [InlineData("kind: bluetooth, default: nope", true)]
    [InlineData("kind: audio, default: bt1", true)] // an item of another kind
    public void ADefaultThatMatchesNoItemOfTheKindIsAWarningNotAnError(string with, bool warns)
    {
        var load = ConfigYaml.Load(NamedLibrary + $"flows:\n  f:\n    nodes:\n      - {{ id: pick, step: prompt.pick, with: {{ {with} }} }}\n");
        Assert.Empty(load.Problems);
        var problems = ConfigValidator.Validate(load.Config!).Where(p => p.Path == "flows.f.nodes.pick.with.default").ToList();

        Assert.Equal(warns, problems.Count == 1 && problems[0].Severity == Severity.Warning);
        Assert.Equal(warns ? 1 : 0, problems.Count);
    }

    [Fact]
    public void OnlyDeviceSettingsTakeTheResultTextStaysAsWritten()
    {
        var step = new StepNode("n", "notify") { With = new([KeyValuePair.Create<string, ParamValue>("text", new ScalarParam("$result:pick"))]) };

        var resolved = PickResults.Resolve(step, new Dictionary<string, string> { ["pick"] = "technics" }, out var missing);

        Assert.Null(missing);
        Assert.Same(step, resolved);
    }

    [Fact]
    public void ABoundDevicePickFillsBluetoothAndAudioSettingsButNotDisplays()
    {
        var yaml = Flow("""
                  - { id: pick, step: prompt.pick, with: { kind: bound } }
                  - { id: a, step: audio.setDefault, with: { device: "$result:pick" }, after: [pick] }
                  - { id: b, step: bluetooth.connect, with: { device: "$result:pick" }, after: [pick] }
                  - { id: d, step: display.layout, with: { layout: "$result:pick" }, after: [pick] }
            """);
        var error = Assert.Single(Errors(yaml));
        Assert.StartsWith("flows.f.nodes.d.with.", error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlowRunCanPassAPickedDeviceToTheCalledFlowsInput()
    {
        var yaml = Library + """
            flows:
              f:
                nodes:
                  - { id: pick, step: prompt.pick, with: { kind: audio } }
                  - { id: call, step: flow.run, with: { flow: g, wait: true, inputs: { out: "$result:pick" } }, after: [pick] }
              g:
                inputs:
                  out: { kind: audio, name: Output, default: technics }
                nodes:
                  - { id: s, step: audio.setDefault, with: { device: $out } }

            """;
        Assert.Empty(Errors(yaml));
        var resolved = PickResults.Resolve((StepNode)ConfigYaml.Load(yaml).Config!.Flows[0].FindNode("call")!,
            new Dictionary<string, string> { ["pick"] = "technics" }, out var missing);
        Assert.Null(missing);
        Assert.Equal("technics", FlowInputs.Given(resolved)["out"]);
    }
}
