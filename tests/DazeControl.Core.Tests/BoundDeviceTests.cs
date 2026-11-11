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

    [Fact]
    public void AnInputCantBeOfKindBound()
    {
        var load = ConfigYaml.Load(Library.Replace("    nodes:", "    inputs: { hp: { kind: bound, default: technics } }\n    nodes:", StringComparison.Ordinal));

        Assert.Contains(load.Problems, p => p.Path == "flows.f.inputs.hp.kind");
    }
}
