using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>Flow inputs (D-064, flow-calls/DESIGN.md §5): YAML, validation, resolution and Library renames.</summary>
public class FlowInputTests
{
    private const string Library = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC" }
            sony: { address: "AC:80:0A:11:22:33" }

        """;

    /// <summary>DESIGN §1 B as written there.</summary>
    private const string ScenarioB = Library + """
        flows:
          make-default:
            name: Make it the default
            glyph: E767
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
            hotkeys: [Alt+Z]
            nodes:
              - id: technics
                step: flow.run
                with: { flow: make-default, inputs: { output: technics, bluetooth: technics } }
                onFailure: skip
              - id: sony
                step: flow.run
                with: { flow: make-default, inputs: { output: sony, bluetooth: sony } }
                after: [{ id: technics, when: failure }]
        """;

    private static Configuration LoadClean(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private static ValueList<Problem> Validate(string yaml) => ConfigValidator.Validate(LoadClean(yaml));

    private static Problem SingleError(string yaml)
    {
        var problem = Assert.Single(Validate(yaml));
        Assert.Equal(Severity.Error, problem.Severity);
        return problem;
    }

    /// <summary>A flow "mk" with one audio input "output" and one step using <paramref name="device"/>.</summary>
    private static string Callee(string device = "$output", string step = "audio.setDefault", string inputs = "output: { kind: audio, name: Output, default: technics }") =>
        Library + $$"""
        flows:
          mk:
            name: Make it the default
            inputs: { {{inputs}} }
            nodes:
              - { id: set, step: {{step}}, with: { device: {{device}} } }

        """;

    // ---- YAML ----

    [Fact]
    public void ScenarioBRoundTripsToTheSameModelAndText()
    {
        var model = LoadClean(ScenarioB);

        var saved = ConfigYaml.Save(model);
        var reloaded = LoadClean(saved);

        Assert.Equal(model, reloaded);
        Assert.Equal(saved, ConfigYaml.Save(reloaded));
        var flow = model.Flows[0];
        Assert.Equal(["output", "bluetooth"], flow.Inputs.Keys);
        Assert.Equal(new FlowInput(LibraryKind.Audio, "Output", "technics"), flow.Inputs["output"]);
        Assert.Equal(new FlowInput(LibraryKind.Bluetooth, "Bluetooth device", "technics"), flow.Inputs["bluetooth"]);
        Assert.Empty(model.Flows[1].Inputs);
        Assert.Contains("inputs:", saved, StringComparison.Ordinal);
        Assert.Contains("$output", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioBIsValid() => Assert.Empty(Validate(ScenarioB));

    [Fact]
    public void AnUnknownKindIsAProblemOnThatFlowAndDropsTheInput()
    {
        var load = ConfigYaml.Load(Library + """
            flows:
              mk:
                inputs: { output: { kind: speaker, default: technics } }
                nodes: []
            """);

        var problem = Assert.Single(load.Problems);
        Assert.Equal(("flows.mk.inputs.output.kind", "mk"), (problem.Path, problem.FlowId));
        Assert.Empty(load.Config!.Flows[0].Inputs);
    }

    [Fact]
    public void ALabelDefaultsToTheName()
    {
        var config = LoadClean(Library + """
            flows:
              mk:
                inputs: { output: { kind: audio, default: technics } }
                nodes: []
            """);

        Assert.Equal("output", config.Flows[0].Inputs["output"].Label);
        Assert.DoesNotContain("name: output", ConfigYaml.Save(config), StringComparison.Ordinal);
    }

    // ---- Validation: the flow's own inputs ----

    [Fact]
    public void AStepUsingItsFlowsInputIsValid() => Assert.Empty(Validate(Callee()));

    [Fact]
    public void AnUnknownInputIsAnErrorOnTheStep()
    {
        var problem = SingleError(Callee("$speaker"));
        Assert.Equal(("flows.mk.nodes.set.with.device", "This flow has no input called \"speaker\"."), (problem.Path, problem.Message));
    }

    [Fact]
    public void AnInputOfAnotherKindIsAnErrorOnTheStep()
    {
        var problem = SingleError(Callee("$output", "bluetooth.connect"));
        Assert.Equal(("flows.mk.nodes.set.with.device", "Output is an audio device; this setting needs a Bluetooth device."),
            (problem.Path, problem.Message));
    }

    [Fact]
    public void AnInputOutsideADeviceSettingIsAnError()
    {
        var problem = SingleError(Library + """
            flows:
              mk:
                inputs: { output: { kind: audio, default: technics } }
                nodes:
                  - { id: say, step: notify, with: { text: $output } }
            """);
        Assert.Equal(("flows.mk.nodes.say.with.text", "Inputs ($output) only go in device settings."), (problem.Path, problem.Message));
    }

    [Theory]
    [InlineData("http.request", "headers: { Device: $output }, url: http://x", "headers")]
    [InlineData("android.keys", "device: tv, keys: [$output]", "keys")]
    public void AnInputInsideAListOrMapIsAnError(string step, string with, string param)
    {
        var problems = Validate(Library + $$"""
            flows:
              mk:
                inputs: { output: { kind: audio, default: technics } }
                nodes:
                  - { id: s, step: {{step}}, with: { {{with}} } }
            """);

        Assert.Contains(problems, p => p.Path == $"flows.mk.nodes.s.with.{param}" && p.Message == "Inputs ($output) only go in device settings.");
    }

    [Fact]
    public void ApplyLeavesOtherMapsAlone()
    {
        var flow = LoadClean(Library + """
            flows:
              mk:
                inputs: { output: { kind: audio, default: technics } }
                nodes:
                  - { id: s, step: http.request, with: { url: http://x, headers: { Device: $output } } }
            """).Flows[0];

        Assert.Equal(flow.Nodes, FlowInputs.Apply(flow, new Dictionary<string, string>()).Nodes);
    }

    [Theory]
    [InlineData("output: { kind: audio, name: Output, default: nope }", "flows.mk.inputs.output.default", "No audio device called \"nope\" in the library.")]
    [InlineData("output: { kind: audio, name: Output }", "flows.mk.inputs.output.default", "Output needs a default device.")]
    [InlineData("Output: { kind: audio, default: technics }", "flows.mk.inputs.Output", "Name inputs with small letters, digits and dashes, starting with a letter (like output).")]
    public void ABadInputIsAnErrorOnTheFlow(string inputs, string path, string message)
    {
        var device = inputs.StartsWith("Output", StringComparison.Ordinal) ? "$Output" : "$output";
        var problems = Validate(Callee(device, inputs: inputs));

        Assert.Contains(problems, p => p.Severity == Severity.Error && p.Path == path && p.Message == message && p.FlowId == "mk");
    }

    // ---- Validation: what a call passes ----

    private static string Caller(string inputs, string callerInputs = "") =>
        Callee() + $$"""
          top:
            name: Headphones
        {{(callerInputs.Length > 0 ? "    inputs: { " + callerInputs + " }\n" : "")}}    nodes:
              - { id: call, step: flow.run, with: { flow: mk, inputs: { {{inputs}} } } }
        """;

    [Fact]
    public void PassingALibraryDeviceIsValid() => Assert.Empty(Validate(Caller("output: sony")));

    [Fact]
    public void PassingAnUnknownDeviceIsAnError()
    {
        var problem = SingleError(Caller("output: bose"));
        Assert.Equal(("flows.top.nodes.call.with.inputs.output", "No audio device called \"bose\" in the library."), (problem.Path, problem.Message));
    }

    [Fact]
    public void PassingAnInputTheCalledFlowDoesntHaveIsAnError()
    {
        var problem = SingleError(Caller("speaker: sony"));
        Assert.Equal(("flows.top.nodes.call.with.inputs.speaker", "Make it the default has no input called \"speaker\"."), (problem.Path, problem.Message));
    }

    [Fact]
    public void PassingThisFlowsOwnInputOfTheSameKindIsValid() =>
        Assert.Empty(Validate(Caller("output: $out", "out: { kind: audio, default: sony }")));

    [Fact]
    public void PassingThisFlowsInputOfAnotherKindIsAnError()
    {
        var problem = SingleError(Caller("output: $bt", "bt: { kind: bluetooth, name: Earbuds, default: sony }"));
        Assert.Equal(("flows.top.nodes.call.with.inputs.output", "Earbuds is a Bluetooth device; Output needs an audio device."), (problem.Path, problem.Message));
    }

    [Fact]
    public void PassingAnInputThisFlowDoesntHaveIsAnError()
    {
        var problem = SingleError(Caller("output: $out"));
        Assert.Equal(("flows.top.nodes.call.with.inputs.output", "This flow has no input called \"out\"."), (problem.Path, problem.Message));
    }

    // ---- Resolution ----

    [Fact]
    public void ApplyFillsCallerValuesElseDefaults()
    {
        var flow = LoadClean(ScenarioB).Flows[0];

        var resolved = FlowInputs.Apply(flow, new Dictionary<string, string> { ["output"] = "sony" });

        Assert.Equal("sony", ((ScalarParam)((StepNode)resolved.Nodes[0]).With["device"]).Text);
        Assert.Equal("technics", ((ScalarParam)((StepNode)resolved.Nodes[1]).With["device"]).Text);
        Assert.Equal("technics", ((ScalarParam)((StepNode)FlowInputs.Apply(flow, new Dictionary<string, string>()).Nodes[0]).With["device"]).Text);
        var plain = LoadClean(ScenarioB).Flows[1];
        Assert.Same(plain, FlowInputs.Apply(plain, new Dictionary<string, string>())); // no inputs: nothing to do
    }

    [Fact]
    public void ApplyResolvesPassThroughValuesOfACall()
    {
        var top = LoadClean(Caller("output: $out", "out: { kind: audio, default: sony }")).Flows[1];

        var resolved = FlowInputs.Apply(top, new Dictionary<string, string>());

        Assert.Equal(new Dictionary<string, string> { ["output"] = "sony" }, FlowInputs.Given((StepNode)resolved.Nodes[0]));
    }

    [Fact]
    public void ACallerValueMissingFromTheLibraryNamesTheInput()
    {
        var config = LoadClean(ScenarioB);

        var message = FlowInputs.Check(config.Flows[0], config.Library, new Dictionary<string, string> { ["output"] = "bose" }, "Headphones");

        Assert.Equal("Output (from Headphones: bose) isn't in your Library anymore.", message);
        Assert.Null(FlowInputs.Check(config.Flows[0], config.Library, new Dictionary<string, string> { ["output"] = "sony" }, "Headphones"));
        Assert.Equal("Make it the default has no input called \"speaker\" anymore.",
            FlowInputs.Check(config.Flows[0], config.Library, new Dictionary<string, string> { ["speaker"] = "sony" }, "Headphones"));
    }

    // ---- Library rename and "used by" ----

    [Fact]
    public void RenameRewritesDefaultsAndPassedValuesOfThatKind()
    {
        var (config, refusal) = LibraryEdits.Rename(LoadClean(ScenarioB), LibraryKind.Audio, "technics", "eah");

        Assert.Equal(RenameRefusal.None, refusal);
        var mk = config.Flows[0];
        Assert.Equal("eah", mk.Inputs["output"].Default);
        Assert.Equal("technics", mk.Inputs["bluetooth"].Default); // the Bluetooth device of the same name stays
        Assert.Equal("$output", ((ScalarParam)((StepNode)mk.Nodes[0]).With["device"]).Text);
        var passed = FlowInputs.Given((StepNode)config.Flows[1].Nodes[0]);
        Assert.Equal(new Dictionary<string, string> { ["output"] = "eah", ["bluetooth"] = "technics" }, passed);
        Assert.Equal(new Dictionary<string, string> { ["output"] = "sony", ["bluetooth"] = "sony" }, FlowInputs.Given((StepNode)config.Flows[1].Nodes[1]));
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void AnInputDefaultCountsAsUse()
    {
        var config = LoadClean(ScenarioB);

        Assert.True(LibraryEdits.Uses(config.Flows[0], config, LibraryKind.Bluetooth, "technics"));
        Assert.True(LibraryEdits.Uses(config.Flows[1], config, LibraryKind.Bluetooth, "sony")); // passed to a call
        Assert.False(LibraryEdits.Uses(config.Flows[0], config, LibraryKind.Bluetooth, "sony"));
    }
}
