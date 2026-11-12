using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>A Bluetooth device and an audio device bound as one (D-116).</summary>
public class BoundDeviceTests
{
    private const string Library = """
        library:
          audio: { technics-out: { match: Technics } }
          bluetooth: { technics-bt: { address: "B8:20:8E:ED:48:FC", name: Technics EAH-AZ80 } }
          bound: { technics: { bluetooth: technics-bt, audio: technics-out, name: Technics headphones } }
        flows:
          f:
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }
              - { id: out, step: audio.setDefault, needs: [bt], with: { device: technics } }

        """;

    private static Configuration Load(string yaml = Library)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    [Fact]
    public void LoadsAndSavesTheBinding()
    {
        var config = Load();

        Assert.Equal(new BoundDevice("technics", "technics-bt", "technics-out", "Technics headphones"), Assert.Single(config.Library.Bound));
        Assert.Equal(config, Load(ConfigYaml.Save(config)));
    }

    [Fact]
    public void BluetoothAndAudioStepsResolveTheirSide()
    {
        var library = Load().Library;

        Assert.Equal("B8:20:8E:ED:48:FC", library.ResolveBluetooth("technics")!.Address);
        Assert.Equal("Technics", library.ResolveAudio("technics")!.Match);
        Assert.True(library.Contains(LibraryKind.Bluetooth, "technics"));
        Assert.Empty(ConfigValidator.Validate(Load()));
    }

    [Fact]
    public void AMissingSideWarnsAndFailsOnlyTheStepsUsingIt()
    {
        var config = Load(Library.Replace("audio: technics-out", "audio: gone", StringComparison.Ordinal));

        var problems = ConfigValidator.Validate(config);

        Assert.Contains(problems, p => p is { Severity: Severity.Warning, Path: "library.bound.technics.audio" });
        var error = Assert.Single(problems, p => p.Severity == Severity.Error);
        Assert.Equal("flows.f.nodes.out.with.device", error.Path);
        Assert.Null(config.Library.ResolveAudio("technics"));
        Assert.NotNull(config.Library.ResolveBluetooth("technics"));
    }

    [Fact]
    public void ANameADeviceAlsoHasIsAnError()
    {
        var config = Load(Library.Replace("bound: { technics:", "bound: { technics-bt:", StringComparison.Ordinal)
            .Replace("device: technics }", "device: technics-out }", StringComparison.Ordinal));

        Assert.Contains(ConfigValidator.Validate(config), p => p is { Severity: Severity.Error, Path: "library.bound.technics-bt" });
    }

    [Fact]
    public void RenamingASideFollowsTheBinding()
    {
        var (config, refusal) = LibraryEdits.Rename(Load(), LibraryKind.Bluetooth, "technics-bt", "az80");

        Assert.Equal(RenameRefusal.None, refusal);
        Assert.Equal("az80", config.Library.Bound[0].Bluetooth);
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void RenamingTheBindingFollowsTheSteps()
    {
        var before = Load();
        Assert.True(LibraryEdits.Uses(before.Flows[0], before, LibraryKind.Bound, "technics"));
        Assert.False(LibraryEdits.Uses(before.Flows[0], before, LibraryKind.Bluetooth, "technics-bt"));

        var (config, refusal) = LibraryEdits.Rename(before, LibraryKind.Bound, "technics", "headphones");

        Assert.Equal(RenameRefusal.None, refusal);
        Assert.All(config.Flows[0].Nodes.OfType<StepNode>(), s => Assert.Equal("headphones", ((ScalarParam)s.With["device"]).Text));
        Assert.Equal(RenameRefusal.Taken, LibraryEdits.Rename(before, LibraryKind.Bound, "technics", "technics-out").Refusal);
    }

    // ---- Bound flow inputs (D-125) ----

    private const string WithInput = """
        library:
          audio: { technics-out: { match: Technics }, sony-out: { match: Sony } }
          bluetooth: { technics-bt: { address: "B8:20:8E:ED:48:FC" }, sony-bt: { address: "AC:80:0A:11:22:33" } }
          bound:
            headphones: { bluetooth: technics-bt, audio: technics-out, name: Technics headphones }
            sony: { bluetooth: sony-bt, audio: sony-out }
        flows:
          f:
            inputs:
              headphones: { kind: bound, name: Headphones, default: headphones }
            nodes:
              - { id: connect, step: bluetooth.connect, with: { device: $headphones } }
              - { id: check, step: bluetooth.check, with: { device: $headphones }, after: [connect] }
              - { id: setDefault, step: audio.setDefault, with: { device: $headphones }, after: [check] }

        """;

    [Fact]
    public void ABoundInputRoundTripsAndValidates()
    {
        var config = Load(WithInput);

        Assert.Equal(new FlowInput(LibraryKind.Bound, "Headphones", "headphones"), config.Flows[0].Inputs["headphones"]);
        Assert.Contains("kind: bound", ConfigYaml.Save(config), StringComparison.Ordinal);
        Assert.Equal(config, Load(ConfigYaml.Save(config)));
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void ABoundInputResolvesToEachStepsSide()
    {
        var config = Load(WithInput);

        foreach (var (given, bt, match) in new[] { ((string?)null, "B8:20:8E:ED:48:FC", "Technics"), ("sony", "AC:80:0A:11:22:33", "Sony") })
        {
            var values = given is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["headphones"] = given };
            var steps = FlowInputs.Apply(config.Flows[0], values).Nodes.OfType<StepNode>().ToDictionary(s => s.Id, s => ((ScalarParam)s.With["device"]).Text);
            Assert.Equal(bt, config.Library.ResolveBluetooth(steps["connect"])!.Address);
            Assert.Equal(bt, config.Library.ResolveBluetooth(steps["check"])!.Address);
            Assert.Equal(match, config.Library.ResolveAudio(steps["setDefault"])!.Match);
        }
        // A caller may pass another bound device, not a plain Bluetooth one.
        Assert.Null(FlowInputs.Check(config.Flows[0], config.Library, new Dictionary<string, string> { ["headphones"] = "sony" }, "Caller"));
        Assert.NotNull(FlowInputs.Check(config.Flows[0], config.Library, new Dictionary<string, string> { ["headphones"] = "sony-bt" }, "Caller"));
    }

    [Fact]
    public void ABoundInputFitsOnlyBluetoothAndAudioSettings()
    {
        var yaml = WithInput.Replace("  - { id: setDefault, step: audio.setDefault, with: { device: $headphones }, after: [check] }",
            "  - { id: setDefault, step: android.input, with: { device: $headphones, input: hdmi1 }, after: [check] }", StringComparison.Ordinal);

        Assert.Contains(ConfigValidator.Validate(Load(yaml)), p => p is { Severity: Severity.Error, Path: "flows.f.nodes.setDefault.with.device" });
    }

    [Fact]
    public void ADeletedDefaultFailsOnlyTheInputAndOtherFlowsStay()
    {
        var yaml = WithInput.Replace("default: headphones", "default: gone", StringComparison.Ordinal) + """
              other:
                nodes:
                  - { id: connect, step: bluetooth.connect, with: { device: sony } }

            """;
        var load = ConfigYaml.Load(yaml);

        Assert.Empty(load.Problems); // the flow still loads
        var error = Assert.Single(ConfigValidator.Validate(load.Config!), p => p.Severity == Severity.Error);
        Assert.Equal("flows.f.inputs.headphones.default", error.Path);
        Assert.Contains("bound device", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultWithoutTheStepsSideFailsThatStep()
    {
        var config = Load(WithInput.Replace("audio: technics-out, name", "audio: gone, name", StringComparison.Ordinal));

        var error = Assert.Single(ConfigValidator.Validate(config), p => p.Severity == Severity.Error);
        Assert.Equal("flows.f.nodes.setDefault.with.device", error.Path);
    }

    [Fact]
    public void RenamingTheBindingFollowsTheInputAndDeleteSeesTheUse()
    {
        var before = Load(WithInput);
        Assert.True(LibraryEdits.Uses(before.Flows[0], before, LibraryKind.Bound, "headphones"));
        Assert.False(LibraryEdits.Uses(before.Flows[0], before, LibraryKind.Bluetooth, "technics-bt"));

        var (config, refusal) = LibraryEdits.Rename(before, LibraryKind.Bound, "headphones", "wh1000");

        Assert.Equal(RenameRefusal.None, refusal);
        Assert.Equal("wh1000", config.Flows[0].Inputs["headphones"].Default);
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void ANewBoundInputIsCalledDevice() =>
        Assert.Equal("device", FlowInputs.NewName(Load(WithInput).Flows[0], LibraryKind.Bound));
}
