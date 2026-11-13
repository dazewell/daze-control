using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>T23.7 (DESIGN §9): text inputs, flow outputs and Make text in the model, YAML and validator.</summary>
public sealed class TextValuesTests
{
    private const string Header = """
        version: 1
        library:
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC", name: Technics }
        flows:
          inner:
            name: Connect headphones
            inputs:
              greeting: { kind: text, name: Greeting, default: Hi }
            outputs:
              device: "{bt.device}"
              said: "{$greeting} at {@time}"
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }

        """;

    private static Configuration Load(string flows)
    {
        var load = ConfigYaml.Load(Header + flows);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private static List<Problem> Problems(string flows) => [.. ConfigValidator.Validate(Load(flows))];

    private static string Caller(string nodes, string extra = "") => $"""
          caller:
            name: Evening
        {extra}    nodes:
        {nodes}
        """;

    [Fact]
    public void TextInputsOutputsAndMakeTextSaveAsWritten()
    {
        var yaml = Header + Caller("""
                  - { id: call, step: flow.run, with: { flow: inner, inputs: { greeting: "Hello {first.device}" } }, after: [first] }
                  - { id: first, step: bluetooth.connect, with: { device: technics } }
                  - { id: line, step: text.make, with: { text: "{call.device} / {call.said}" }, after: [call] }
            """);
        var config = ConfigYaml.Load(yaml).Config!;
        var inner = config.Flows.Single(f => f.Id == "inner");

        Assert.True(inner.Inputs["greeting"].IsText);
        Assert.Null(inner.Inputs["greeting"].Kind);
        Assert.Equal("Hi", inner.Inputs["greeting"].Default);
        Assert.Equal(["device", "said"], inner.Outputs.Keys);
        var saved = ConfigYaml.Save(config);
        Assert.Contains("kind: text", saved, StringComparison.Ordinal);
        Assert.Contains("outputs:", saved, StringComparison.Ordinal);
        Assert.Equal(saved, ConfigYaml.Save(ConfigYaml.Load(saved).Config!));
    }

    [Fact]
    public void AFlowWithoutOutputsOrTextInputsWritesNoNewKeys()
    {
        var saved = ConfigYaml.Save(Load(Caller("      - { id: n, step: notify, with: { text: hi } }")) with { Flows = [] });
        Assert.DoesNotContain("outputs", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallsOutputsAreItsValues()
    {
        var problems = Problems(Caller("""
                  - { id: call, step: flow.run, with: { flow: inner } }
                  - { id: note, step: notify, with: { text: "{call.device} {call.said}" }, after: [call] }
            """));

        Assert.DoesNotContain(problems, p => p.Severity >= Severity.Warning && p.FlowId == "caller");
        var config = Load(Caller("      - { id: call, step: flow.run, with: { flow: inner } }"));
        var caller = config.Flows.Single(f => f.Id == "caller");
        var call = (StepNode)caller.FindNode("call")!;
        var values = StepValues.Of(caller, call, ProviderCatalog.Find(ProviderCatalog.RunFlowId)!, id => config.Flows.FirstOrDefault(f => f.Id == id));
        Assert.Equal(["device", "said", "error"], values.Select(v => v.Name));
        Assert.Equal("Device", values[0].Title);
    }

    [Fact]
    public void AnOutputTheCalledFlowDoesntGiveIsAWarningNamingWhatItShows()
    {
        var problem = Assert.Single(Problems(Caller("""
                  - { id: call, step: flow.run, with: { flow: inner } }
                  - { id: note, step: notify, with: { text: "{call.nmae|someone}" }, after: [call] }
            """)), p => p.FlowId == "caller");

        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Equal("Connect headphones doesn't give a value called \"nmae\", so {call.nmae|someone} shows \"someone\". Add it to Connect headphones's values, or pick one it gives.", problem.Message);
    }

    [Fact]
    public void ACallThatDoesntWaitGivesOnlyItsError()
    {
        var problem = Assert.Single(Problems(Caller("""
                  - { id: call, step: flow.run, with: { flow: inner, wait: false } }
                  - { id: note, step: notify, with: { text: "{call.device}" }, after: [call] }
            """)), p => p.FlowId == "caller");

        Assert.Equal(Severity.Error, problem.Severity);
        Assert.StartsWith("Run flow gives no value called \"device\"", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassedTextIsCheckedLikeASettingThatTakesValues()
    {
        var problems = Problems(Caller("""
                  - { id: call, step: flow.run, with: { flow: inner, inputs: { greeting: "Hi {later.device}" } } }
                  - { id: later, step: bluetooth.connect, with: { device: technics }, after: [call] }
            """));

        var problem = Assert.Single(problems, p => p.FlowId == "caller");
        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal("flows.caller.nodes.call.with.inputs.greeting", problem.Path);
        Assert.Contains("must run before this step", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassedTextIsAtMostOneValueLong()
    {
        var problems = Problems(Caller($"      - {{ id: call, step: flow.run, with: {{ flow: inner, inputs: {{ greeting: \"{new string('x', 1001)}\" }} }} }}"));

        Assert.Contains(problems, p => p is { Severity: Severity.Error, Path: "flows.caller.nodes.call.with.inputs.greeting" }
            && p.Message == "Greeting holds at most 1000 characters.");
    }

    [Fact]
    public void ATextInputCantFillADeviceSettingNorBeAsked()
    {
        var problems = Problems(Caller("      - { id: bt, step: bluetooth.connect, with: { device: $note } }", """
                inputs:
                  note: { kind: text, name: Note, default: x, ask: true }

            """));

        Assert.Contains(problems, p => p.Severity == Severity.Error && p.Path == "flows.caller.nodes.bt.with.device"
            && p.Message.StartsWith("Note is text; this setting needs a Bluetooth device", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Severity == Severity.Error && p.Path == "flows.caller.inputs.note.ask");
    }

    [Fact]
    public void ATextInputMayBeEmptyAndIsUsedInText()
    {
        var problems = Problems(Caller("      - { id: n, step: notify, with: { text: \"{$note} done\" } }", """
                inputs:
                  note: { kind: text, name: Note, default: "" }

            """));

        Assert.DoesNotContain(problems, p => p.FlowId == "caller");
    }

    [Fact]
    public void OutputsAreNamedAndChecked()
    {
        var problems = Problems(Caller("      - { id: bt, step: bluetooth.connect, with: { device: technics }, onFailure: ignore }", """
                outputs:
                  Bad: "x"
                  error: "x"
                  sure: "{bt.error}"
                  maybe: "{bt.device}"
                  fine: "{bt.device|none} {bt.nmae}"

            """));

        string[] Of(string name) => [.. problems.Where(p => p.Path == $"flows.caller.outputs.{name}").Select(p => p.Severity + ": " + p.Message)];
        Assert.StartsWith("Error: Name outputs", Assert.Single(Of("Bad")), StringComparison.Ordinal);
        Assert.StartsWith("Error: Name outputs", Assert.Single(Of("error")), StringComparison.Ordinal);
        Assert.Equal("Warning: \"Connect\" gives it only when it fails, so Why it failed can be empty. Add what to show when it has no value.", Assert.Single(Of("sure")));
        Assert.StartsWith("Warning: \"Connect\" may fail and the flow carry on", Assert.Single(Of("maybe")), StringComparison.Ordinal);
        Assert.StartsWith("Error: Connect gives no value called \"nmae\"", Assert.Single(Of("fine")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutputOfAStepEveryRunReachesNeedsNoFallback()
    {
        var problems = Problems(Caller("""
                  - { id: bt, step: bluetooth.connect, with: { device: technics } }
                  - { id: next, step: bluetooth.connect, with: { device: technics }, after: [bt] }
                  - { id: fix, step: bluetooth.connect, with: { device: technics }, after: [{ id: next, when: failure }] }
            """, """
                outputs:
                  device: "{bt.device}"
                  fixed: "{fix.device}"

            """));

        Assert.DoesNotContain(problems, p => p.Path == "flows.caller.outputs.device");
        Assert.Contains(problems, p => p is { Severity: Severity.Warning, Path: "flows.caller.outputs.fixed" });
    }

    [Fact]
    public void MakeTextOfASensitiveValueIsSensitive()
    {
        var config = Load(Caller("""
                  - { id: p, step: process.run, with: { path: x.exe } }
                  - { id: line, step: text.make, with: { text: "Got {p.output}" }, after: [p] }
                  - { id: plain, step: text.make, with: { text: "Code {p.exitCode}" }, after: [p] }
            """, """
                outputs:
                  said: "{line.text}"

            """));
        var caller = config.Flows.Single(f => f.Id == "caller");
        var make = ProviderCatalog.Find(ProviderCatalog.MakeTextId)!;

        Assert.True(StepValues.Find(caller, (StepNode)caller.FindNode("line")!, make, "text")!.Sensitive);
        Assert.False(StepValues.Find(caller, (StepNode)caller.FindNode("plain")!, make, "text")!.Sensitive);
        // A call of a flow whose output holds it is Sensitive too, one level up.
        var top = new Flow("top", "Top", [new StepNode("c", ProviderCatalog.RunFlowId) { With = new([KeyValuePair.Create("flow", (ParamValue)"caller")]) }]);
        Assert.True(StepValues.Find(top, (StepNode)top.FindNode("c")!, ProviderCatalog.Find(ProviderCatalog.RunFlowId)!, "said",
            id => config.Flows.FirstOrDefault(f => f.Id == id))!.Sensitive);
    }

    [Fact]
    public void PassingATextInputOnPassesItAsLiteralText()
    {
        var middle = new Flow("m", "Middle", [new StepNode("c", ProviderCatalog.RunFlowId)
        {
            With = new([KeyValuePair.Create("flow", (ParamValue)"inner"),
                KeyValuePair.Create(FlowInputs.Param, (ParamValue)new MapParam(new([KeyValuePair.Create("greeting", (ParamValue)"$greeting")])))]),
        }])
        { Inputs = new([KeyValuePair.Create("greeting", new FlowInput(null, "Greeting", "Hey {x.y}"))]) };

        var given = FlowInputs.Given((StepNode)FlowInputs.Apply(middle, new Dictionary<string, string> { ["greeting"] = "{bt.device}" }).FindNode("c")!);
        var fallback = FlowInputs.Given((StepNode)FlowInputs.Apply(middle, new Dictionary<string, string>()).FindNode("c")!);

        Assert.Equal("{{bt.device}", given["greeting"]);
        Assert.Equal("Hey {{x.y}", fallback["greeting"]);
    }

    [Fact]
    public void RenamingAnOutputFollowsThroughToItsCallers()
    {
        var config = Load(Caller("""
                  - { id: call, step: flow.run, with: { flow: inner } }
                  - { id: note, step: notify, with: { text: "{call.device|?} {{call.device} {call.said}" }, after: [call] }
            """, """
                outputs:
                  again: "{call.device}"

            """));

        var renamed = FlowOutputs.Rename(config, "inner", "device", "headphones");
        var caller = renamed.Flows.Single(f => f.Id == "caller");

        Assert.Equal(["headphones", "said"], renamed.Flows.Single(f => f.Id == "inner").Outputs.Keys);
        Assert.Equal("{call.headphones|?} {{call.device} {call.said}", ((ScalarParam)((StepNode)caller.FindNode("note")!).With["text"]).Text);
        Assert.Equal("{call.headphones}", caller.Outputs["again"]);
        Assert.Same(config, FlowOutputs.Rename(config, "inner", "device", "said")); // taken
        Assert.Same(config, FlowOutputs.Rename(config, "inner", "device", "Bad"));
        Assert.Equal(["caller"], FlowOutputs.Users(config, "inner", "device").Select(f => f.Id));
    }

    [Fact]
    public void RemovingAnOutputLeavesItsCallersTheFallback()
    {
        var config = Load(Caller("""
                  - { id: call, step: flow.run, with: { flow: inner } }
                  - { id: note, step: notify, with: { text: "[{call.device|none}] [{call.device}]" }, after: [call] }
            """));

        var caller = FlowOutputs.Remove(config, "inner", "device").Flows.Single(f => f.Id == "caller");

        Assert.Equal("[none] []", ((ScalarParam)((StepNode)caller.FindNode("note")!).With["text"]).Text);
    }

    [Fact]
    public void DeletingAStepDropsItsValuesFromOutputsAndCallsToo()
    {
        var config = Load(Caller("""
                  - { id: first, step: bluetooth.connect, with: { device: technics } }
                  - { id: call, step: flow.run, with: { flow: inner, inputs: { greeting: "Hi {first.device|you}" } }, after: [first] }
            """, """
                outputs:
                  dev: "{first.device|-}"

            """));

        var flow = StepValues.DropRefsTo(config.Flows.Single(f => f.Id == "caller"), new HashSet<string> { "first" });

        Assert.Equal("-", flow.Outputs["dev"]);
        Assert.Equal("Hi you", FlowInputs.Given((StepNode)flow.FindNode("call")!)["greeting"]);
    }

    [Fact]
    public void RemovingATextInputLeavesItsDefaultText()
    {
        var config = Load(Caller("      - { id: n, step: notify, with: { text: \"{$note}!\" } }", """
                inputs:
                  note: { kind: text, name: Note, default: "a {b" }
                outputs:
                  said: "{$note}"

            """));
        var caller = config.Flows.Single(f => f.Id == "caller");

        Assert.True(FlowInputs.IsUsed(caller, "note"));
        var detached = FlowInputs.Detach(caller, "note");

        Assert.Equal("a {b!", ((ScalarParam)((StepNode)detached.FindNode("n")!).With["text"]).Text);
        Assert.Equal("a {b", detached.Outputs["said"]);
    }

    [Fact]
    public void FilledOutputsAndTextInputsAreCut()
    {
        var flow = Load("").Flows.Single(f => f.Id == "inner");
        var values = new RunValues();
        values.SetDevice("bt", "device", "technics", LibraryKind.Bluetooth);
        values.SetInputText("greeting", new string('x', 2000), sensitive: true);
        var library = Load("").Library;

        var outputs = FlowOutputs.Fill(flow, values, library);

        Assert.Equal("Technics", outputs["device"]);
        Assert.Equal(RunValues.MaxValueLength, outputs["said"].Length);
        Assert.Equal(RunValues.MaxValueLength, Assert.Single(values.SensitiveInputs()).Text.Length);
    }

    [Theory]
    [InlineData("device", "Device")]
    [InlineData("deviceName", "Device name")]
    [InlineData("x2", "X2")]
    public void AnOutputsTitleComesFromItsName(string name, string title) => Assert.Equal(title, FlowOutputs.Title(name));
}
