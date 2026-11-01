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
