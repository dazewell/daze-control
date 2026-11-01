using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

public class ConfigYamlTests
{
    [Fact]
    public void ParsesFlowsAndHotkeysThroughStaticContext()
    {
        const string yaml = """
            version: 1
            settings: { popup: { position: BottomCenter, seconds: 3 } }
            flows:
              connect-technics:
                name: Connect Technics
                hotkeys: [Alt+Z]
                whenRunning: ignore
                nodes:
                  - { id: bt, step: bluetooth.connect, with: { device: technics }, timeout: 20s }
            """;

        var config = ConfigYaml.Parse(yaml);

        Assert.Equal(1, config.Version);
        var flow = Assert.Single(config.Flows);
        Assert.Equal("connect-technics", flow.Key);
        Assert.Equal("Connect Technics", flow.Value.Name);
        Assert.Equal(["Alt+Z"], flow.Value.Hotkeys);
    }

    [Fact]
    public void EmptyDocumentGivesEmptyConfig() => Assert.Empty(ConfigYaml.Parse("").Flows);
}
