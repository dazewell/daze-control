using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

public class SampleConfigTests
{
    private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    public static TheoryData<string> Samples() =>
        [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Samples"), "*.yaml").Select(f => Path.GetFileName(f))];

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleLoadsAndValidatesClean(string name)
    {
        var load = ConfigYaml.Load(Sample(name));

        Assert.Empty(load.Problems);
        Assert.Empty(ConfigValidator.Validate(load.Config!));
    }

    /// <summary>Steps that change nothing on the PC: what the dev samples (scripts/dev-run.ps1 -Sample) may use.</summary>
    private static readonly HashSet<string> ReadOnlySteps = new(StringComparer.Ordinal)
    {
        "notify", "wait", "flag.set", "flag.check", "prompt.ask", "prompt.pick", "menu.show", "flow.run", "flow.stop",
        "audio.check", "bluetooth.check", "display.check", "display.count", "process.check", "file.check", "file.space",
        "network.wifi", "network.cable", "network.internet", "network.metered", "network.vpn",
        "power.source", "power.battery", "pc.idle", "pc.locked", "time.check", "value.check",
    };

    public static TheoryData<string> DevSamples() =>
        [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Samples"), "dev-*.yaml").Select(f => Path.GetFileName(f))];

    [Theory]
    [MemberData(nameof(DevSamples))]
    public void DevSamplesOnlyCheckShowWaitOrAsk(string name)
    {
        var config = ConfigYaml.Load(Sample(name)).Config!;

        var steps = config.Flows.SelectMany(f => f.Nodes).OfType<StepNode>().Select(n => n.Step).Distinct().ToList();
        Assert.All(steps, step => Assert.Contains(step, ReadOnlySteps));
        if (name == "dev-config.yaml")
            return; // the default dev config tries triggers and a hotkey on purpose
        Assert.All(config.Flows, f => Assert.Empty(f.Triggers)); // nothing fires by itself in the larger samples...
        Assert.All(config.Flows, f => Assert.Empty(f.Hotkeys)); // ...or takes a key from the user's own setup
    }

    [Fact]
    public void UserConfigKeepsTheTechnicsFlowAndBothBluetoothDevices()
    {
        var config = ConfigYaml.Load(Sample("user-config.yaml")).Config!;

        var flow = Assert.Single(config.Flows);
        Assert.Equal("Connect Technics", flow.Name);
        Assert.Equal(["Alt+Z"], flow.Hotkeys);
        Assert.Equal(["bluetooth.connect", "audio.setDefault"], flow.Nodes.Cast<StepNode>().Select(n => n.Step));
        Assert.Equal([new Edge("bt")], flow.Nodes[1].After);
        Assert.Equal([new BluetoothDevice("technics", "B8:20:8E:ED:48:FC", "Technics EAH-AZ80"), new BluetoothDevice("sony", "80:99:E7:2A:AB:A1")],
            config.Library.Bluetooth);
        Assert.Equal([new AudioDevice("technics", "Technics EAH-AZ80")], config.Library.Audio);
    }
}
