using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

public class AndroidInputEditsTests
{
    private static readonly Configuration Config = ConfigYaml.Load("""
        library:
          android:
            tv: { address: 192.168.1.50, inputs: { hdmi1: [], pc: [down, down] } }
            den: { address: 192.168.1.51, inputs: { pc: [up] } }
        flows:
          a:
            nodes:
              - { id: in, step: android.input, with: { device: tv, input: pc } }
              - { id: den, step: android.input, with: { device: den, input: pc }, after: [in] }
          b:
            inputs:
              screen: { kind: android, default: tv }
            nodes:
              - { id: in, step: android.input, with: { device: $screen, input: pc } }
          c:
            nodes:
              - { id: in, step: android.input, with: { device: tv, input: hdmi1 } }
        """).Config!;

    [Fact]
    public void TheFixtureLoads() => Assert.Equal(3, Config.Flows.Count);

    [Fact]
    public void RenameKeepsThePlaceAndTheStepsFollow()
    {
        var (config, refusal) = AndroidInputEdits.Rename(Config, "tv", "pc", " desk ");

        Assert.Equal(InputEditRefusal.None, refusal);
        var tv = config.Library.Android.First(d => d.Key == "tv");
        Assert.Equal(["hdmi1", "desk"], tv.Inputs.Keys);
        Assert.Equal(["down", "down"], tv.Inputs["desk"]);
        Assert.Equal("desk", Input(config, "a", "in"));
        Assert.Equal("pc", Input(config, "a", "den")); // the other device's input of the same name stays
        Assert.Equal("desk", Input(config, "b", "in")); // through the flow's input whose default is tv
        Assert.Equal("hdmi1", Input(config, "c", "in"));
        Assert.Equal(Config.Flows[2], config.Flows[2]);
    }

    [Theory]
    [InlineData("tv", "pc", "  ", InputEditRefusal.Empty)]
    [InlineData("tv", "pc", "hdmi1", InputEditRefusal.Taken)]
    [InlineData("tv", "nope", "x", InputEditRefusal.Missing)]
    [InlineData("nope", "pc", "x", InputEditRefusal.Missing)]
    [InlineData("tv", "pc", "pc", InputEditRefusal.None)]
    public void RenameRefusesWithoutChanging(string device, string name, string newName, InputEditRefusal expected)
    {
        var (config, refusal) = AndroidInputEdits.Rename(Config, device, name, newName);

        Assert.Equal(expected, refusal);
        Assert.Same(Config, config);
    }

    [Fact]
    public void AddPutsANewInputWithNoMovesLast()
    {
        var device = Config.Library.Android[0];
        var name = AndroidInputEdits.NewName(device);
        var (config, refusal) = AndroidInputEdits.Add(Config, "tv", name);

        Assert.Equal("input", name);
        Assert.Equal(InputEditRefusal.None, refusal);
        var tv = config.Library.Android[0];
        Assert.Equal(["hdmi1", "pc", "input"], tv.Inputs.Keys);
        Assert.Empty(tv.Inputs["input"]);
        Assert.Equal("input-2", AndroidInputEdits.NewName(tv));
        Assert.Equal(InputEditRefusal.Taken, AndroidInputEdits.Add(config, "tv", "input").Refusal);
        Assert.Equal(InputEditRefusal.Empty, AndroidInputEdits.Add(config, "tv", " ").Refusal);
        Assert.Equal(InputEditRefusal.Missing, AndroidInputEdits.Add(config, "nope", "x").Refusal);
    }

    [Fact]
    public void RemoveIsRefusedWhileAFlowPicksIt()
    {
        Assert.Equal(InputEditRefusal.InUse, AndroidInputEdits.Remove(Config, "tv", "pc").Refusal);
        Assert.Equal(InputEditRefusal.InUse, AndroidInputEdits.Remove(Config, "tv", "hdmi1").Refusal);

        var unused = Config with { Flows = [Config.Flows[0]] }; // only a's steps: tv/pc, den/pc
        var (config, refusal) = AndroidInputEdits.Remove(unused, "tv", "hdmi1");

        Assert.Equal(InputEditRefusal.None, refusal);
        Assert.Equal(["pc"], config.Library.Android[0].Inputs.Keys);
    }

    [Fact]
    public void UsersCountTheFlowsThatPickIt()
    {
        Assert.Equal(["a", "b"], AndroidInputEdits.Users(Config, "tv", "pc").Select(f => f.Id));
        Assert.Equal(["a"], AndroidInputEdits.Users(Config, "den", "pc").Select(f => f.Id));
        Assert.Equal(["c"], AndroidInputEdits.Users(Config, "tv", "hdmi1").Select(f => f.Id));
    }

    [Fact]
    public void SetMovesKeepsTheOrderAndTakesOnlyUpOrDown()
    {
        var (config, refusal) = AndroidInputEdits.SetMoves(Config, "tv", "hdmi1", ["up", "down", "down"]);

        Assert.Equal(InputEditRefusal.None, refusal);
        Assert.Equal(["up", "down", "down"], config.Library.Android[0].Inputs["hdmi1"]);
        Assert.Equal(["hdmi1", "pc"], config.Library.Android[0].Inputs.Keys);
        Assert.Equal(InputEditRefusal.BadMove, AndroidInputEdits.SetMoves(Config, "tv", "pc", ["left"]).Refusal);
        Assert.Equal(InputEditRefusal.Missing, AndroidInputEdits.SetMoves(Config, "tv", "nope", []).Refusal);
    }

    [Fact]
    public void EditedInputsRoundTripThroughYaml()
    {
        var config = AndroidInputEdits.Add(Config, "tv", "game").Config;
        config = AndroidInputEdits.SetMoves(config, "tv", "game", ["up"]).Config;

        var loaded = ConfigYaml.Load(ConfigYaml.Save(config)).Config!;

        Assert.Equal(["hdmi1", "pc", "game"], loaded.Library.Android[0].Inputs.Keys);
        Assert.Empty(loaded.Library.Android[0].Inputs["hdmi1"]);
        Assert.Equal(["up"], loaded.Library.Android[0].Inputs["game"]);
    }

    private static string Input(Configuration config, string flow, string node) =>
        Assert.IsType<ScalarParam>(((StepNode)config.Flows.First(f => f.Id == flow).FindNode(node)!).With["input"]).Text;
}
