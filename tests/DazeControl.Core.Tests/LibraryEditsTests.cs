using DazeControl.Core.Model;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

public class LibraryEditsTests
{
    private static readonly Configuration Config = ConfigYaml.Load("""
        library:
          bluetooth: { buds: { address: "B8:20:8E:ED:48:FC" } }
          audio: { buds: { match: Technics } }
        flows:
          a:
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: buds } }
              - { id: out, step: audio.setDefault, with: { device: buds }, after: [bt] }
          b:
            nodes:
              - { id: off, step: bluetooth.disconnect, with: { device: buds } }
        """).Config!;

    [Fact]
    public void RenameRewritesTheStepsOfThatKindOnly()
    {
        var (config, refusal) = LibraryEdits.Rename(Config, LibraryKind.Bluetooth, "buds", " technics ");

        Assert.Equal(RenameRefusal.None, refusal);
        Assert.Equal("technics", Assert.Single(config.Library.Bluetooth).Key);
        Assert.Equal("buds", Assert.Single(config.Library.Audio).Key);
        Assert.Equal("technics", Param(config, "a", "bt"));
        Assert.Equal("buds", Param(config, "a", "out")); // an audio device of the same name stays
        Assert.Equal("technics", Param(config, "b", "off"));
        Assert.Equal(Config.Flows[0].Nodes[1], config.Flows[0].Nodes[1]);
    }

    [Theory]
    [InlineData("buds", "  ", RenameRefusal.Empty)]
    [InlineData("nope", "x", RenameRefusal.Missing)]
    [InlineData("buds", "buds", RenameRefusal.None)]
    public void RenameRefusesWithoutChanging(string key, string newKey, RenameRefusal expected)
    {
        var (config, refusal) = LibraryEdits.Rename(Config, LibraryKind.Bluetooth, key, newKey);

        Assert.Equal(expected, refusal);
        Assert.Same(Config, config);
    }

    [Fact]
    public void RenameToATakenKeyIsRefused()
    {
        var config = Config with { Library = Config.Library with { Bluetooth = [.. Config.Library.Bluetooth, new BluetoothDevice("sony", "80:99:E7:2A:AB:A1")] } };

        Assert.Equal(RenameRefusal.Taken, LibraryEdits.Rename(config, LibraryKind.Bluetooth, "buds", "sony").Refusal);
    }

    private static string Param(Configuration config, string flow, string node) =>
        Assert.IsType<ScalarParam>(((StepNode)config.Flows.First(f => f.Id == flow).FindNode(node)!).With["device"]).Text;
}
