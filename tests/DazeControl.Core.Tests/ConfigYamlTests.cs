using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

public class ConfigYamlTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Configuration LoadClean(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return Assert.IsType<Configuration>(load.Config);
    }

    private static Problem SingleProblem(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        return Assert.Single(load.Problems);
    }

    [Fact]
    public void FullFixtureRoundTripsToTheSameModelAndText()
    {
        var model = LoadClean(Fixture("full.yaml"));

        var saved = ConfigYaml.Save(model);
        var reloaded = LoadClean(saved);

        Assert.Equal(model, reloaded);
        Assert.Equal(saved, ConfigYaml.Save(reloaded));
    }

    [Fact]
    public void RetitledStepsKeepTheirSavedIds()
    {
        // D-102 renamed titles only: the ids a saved flow holds are the contract, so old files load and save identically.
        string[] ids =
        [
            "audio.setDefault", "audio.check", "audio.setVolume", "audio.mute", "bluetooth.connect", "bluetooth.disconnect", "bluetooth.check",
            "display.topology", "display.outputs", "display.layout", "process.run", "process.check", "script.powershell", "http.request",
            "android.input", "android.keys", "android.sleep", "android.start", "android.connect", "service.start", "service.stop",
            "service.check", "file.check", "wait", "notify", "flag.set", "flag.check", "flow.run", "menu.show",
            "process.close", "android.stop", "display.screensOff", "android.open", "android.media", "android.setVolume", "android.changeVolume",
            "android.mute", "android.setBrightness", "android.wake", "display.check", "android.check", "prompt.ask",
        ];
        Assert.Equal(ids.Order(), DazeControl.Core.Catalog.ProviderCatalog.All.Select(d => d.Id).Order());

        var yaml = "version: 1"+Environment.NewLine+"flows:"+Environment.NewLine+"  f:"+Environment.NewLine+"    nodes:"+Environment.NewLine;
        yaml += string.Concat(ids.Select((id, i) => $"      - {{ id: s{i}, step: {id} }}" + Environment.NewLine));
        var saved = ConfigYaml.Save(ConfigYaml.Load(yaml).Config!);
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
            Assert.Matches($@"id: s{i}\W+step: {System.Text.RegularExpressions.Regex.Escape(id)}\b", saved);
        Assert.Equal(saved, ConfigYaml.Save(ConfigYaml.Load(saved).Config!));
    }

    [Fact]
    public void ReadsSettingsAndLibrary()
    {
        var config = LoadClean(Fixture("full.yaml"));

        Assert.Equal(new PopupSettings(PopupPosition.TopRight, 5), config.Settings.Popup);
        Assert.Equal([new AudioDevice("technics", "Technics EAH-AZ80"), new AudioDevice("desk-mic", "Yeti", AudioFlow.Input)],
            config.Library.Audio);
        Assert.Equal([new BluetoothDevice("technics", "B8:20:8E:ED:48:FC", "Technics EAH-AZ80"), new BluetoothDevice("sony", "80:99:E7:2A:AB:A1")],
            config.Library.Bluetooth);
    }

    [Fact]
    public void ReadsTheTechnicsFlow()
    {
        var flow = LoadClean(Fixture("full.yaml")).Flows[0];

        var expected = new Flow("connect-technics", "Connect Technics",
        [
            new StepNode("bt", "bluetooth.connect") { With = new([KeyValuePair.Create("device", (ParamValue)"technics")]), Timeout = TimeSpan.FromSeconds(20) },
            new StepNode("out", "audio.setDefault") { With = new([KeyValuePair.Create("device", (ParamValue)"technics")]), After = [new Edge("bt")] },
        ])
        { Hotkeys = ["Alt+Z"] };
        Assert.Equal(expected, flow);
    }

    [Fact]
    public void ReadsFlowSettingsEdgesCardsAndPolicies()
    {
        var config = LoadClean(Fixture("full.yaml"));
        var tv = config.Flows[1];

        Assert.Equal(["connect-technics", "tv-mode", "hidden"], config.Flows.Select(f => f.Id));
        Assert.Equal("E7F4", tv.Glyph);
        Assert.Equal(new TraySettings { Folder = "Living room", Separator = true }, tv.Tray);
        Assert.Equal(WhenRunning.Restart, tv.WhenRunning);
        Assert.False(tv.Popup);
        var hp = Assert.IsType<StepNode>(tv.FindNode("hp"));
        Assert.Equal("Headphones", hp.Name);
        Assert.Equal(FailurePolicy.Skip, hp.OnFailure);
        Assert.Equal(2, hp.RetryCount);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), hp.RetryDelay);
        Assert.Equal([new Edge("hp", EdgeWhen.Failure)], tv.FindNode("buds")!.After);
        var card = Assert.IsType<GroupNode>(tv.FindNode("card"));
        Assert.Equal(["lights", "tv"], card.Members);
        Assert.Equal("Lights and TV", card.Name);
        Assert.Equal([new Edge("card"), new Edge("buds", EdgeWhen.Always)], tv.FindNode("done")!.After);

        var hidden = config.Flows[2];
        Assert.Equal("hidden", hidden.Name);
        Assert.False(hidden.Enabled);
        Assert.False(hidden.Tray.Show);
        Assert.Equal(WhenRunning.Queue, hidden.WhenRunning);
    }

    [Fact]
    public void ReadsParamShapesAndKeepsAwkwardScalarsThroughSave()
    {
        var tv = LoadClean(ConfigYaml.Save(LoadClean(Fixture("full.yaml")))).Flows[1];

        var lights = Assert.IsType<StepNode>(tv.FindNode("lights")).With;
        var headers = Assert.IsType<MapParam>(lights["headers"]).Entries;
        Assert.Equal(new ScalarParam("a: b"), headers["X-Note"]);
        var tvStep = Assert.IsType<StepNode>(tv.FindNode("tv")).With;
        Assert.Equal(new ScalarParam(@"C:\Tools\tv.exe"), tvStep["path"]);
        Assert.Equal(new ListParam(["--input", "hdmi2", ""]), tvStep["args"]);
        Assert.Equal(new ScalarParam("#ready"), Assert.IsType<StepNode>(tv.FindNode("done")).With["text"]);
    }

    [Fact]
    public void SaveOmitsDefaults()
    {
        var config = new Configuration { Flows = [new Flow("f", "f", [new StepNode("w", "wait")])] };

        var yaml = ConfigYaml.Save(config);

        Assert.Equal("""
            version: 1
            flows:
              f:
                nodes:
                  - id: w
                    step: wait

            """.ReplaceLineEndings("\n"), yaml.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EmptyDocumentGivesEmptyConfig() => Assert.Equal(Configuration.Empty, LoadClean(""));

    [Fact]
    public void BrokenYamlGivesNoConfigAndOneFileProblemWithTheLine()
    {
        var load = ConfigYaml.Load("version: 1\nflows: [\n");

        Assert.Null(load.Config);
        var problem = Assert.Single(load.Problems);
        Assert.Null(problem.FlowId);
        Assert.StartsWith("Line ", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerVersionGivesNoConfig()
    {
        var load = ConfigYaml.Load("version: 2");

        Assert.Null(load.Config);
        Assert.Equal("version", Assert.Single(load.Problems).Path);
    }

    [Theory]
    [InlineData("timeout: 20", "flows.f.nodes.a.timeout")]
    [InlineData("onFailure: maybe", "flows.f.nodes.a.onFailure")]
    [InlineData("onFailure: 1", "flows.f.nodes.a.onFailure")]
    [InlineData("after: [{ id: b, when: sometimes }]", "flows.f.nodes.a.after")]
    [InlineData("retry: { count: x }", "flows.f.nodes.a.retry.count")]
    [InlineData("group: [b, c]", "flows.f.nodes.a")]
    public void BadNodeFieldsBecomeProblemsOnTheirFlow(string field, string path)
    {
        var problem = SingleProblem($$"""
            flows:
              f:
                nodes:
                  - { id: a, step: wait, {{field}} }
            """);

        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal(path, problem.Path);
        Assert.Equal("f", problem.FlowId);
    }

    [Theory]
    [InlineData("{ step: wait }", "flows.f.nodes[1].id")]
    [InlineData("{ id: b }", "flows.f.nodes.b.step")]
    public void UnreadableNodesAreDroppedWithAProblem(string node, string path)
    {
        var load = ConfigYaml.Load($"""
            flows:
              f:
                nodes:
                  - {"{ id: a, step: wait }"}
                  - {node}
            """);

        Assert.Equal(path, Assert.Single(load.Problems).Path);
        Assert.Equal(["a"], load.Config!.Flows[0].Nodes.Select(n => n.Id));
    }

    [Fact]
    public void CardWithStepFieldsIsAProblem()
    {
        var problem = SingleProblem("flows: { f: { nodes: [{ id: g, group: [a, b], timeout: 5s }] } }");

        Assert.Equal("flows.f.nodes.g", problem.Path);
    }

    [Theory]
    [InlineData("enabled: yes", "flows.f.enabled")]
    [InlineData("whenRunning: twice", "flows.f.whenRunning")]
    [InlineData("tray: { separator: 1 }", "flows.f.tray.separator")]
    public void BadFlowSettingsBecomeProblemsOnTheirFlow(string field, string path)
    {
        var problem = SingleProblem($"flows: {{ f: {{ {field}, nodes: [] }} }}");

        Assert.Equal(path, problem.Path);
        Assert.Equal("f", problem.FlowId);
    }

    [Theory]
    [InlineData("settings: { popup: { seconds: 0 } }", "settings.popup.seconds")]
    [InlineData("settings: { popup: { position: Middle } }", "settings.popup.position")]
    [InlineData("library: { bluetooth: { x: { name: X } } }", "library.bluetooth.x.address")]
    [InlineData("library: { audio: { x: { flow: input } } }", "library.audio.x.match")]
    [InlineData("library: { audio: { x: { match: X, flow: both } } }", "library.audio.x.flow")]
    [InlineData("library: { android: { tv: { inputs: { a: [up] } } } }", "library.android.tv.address")]
    [InlineData("library: { android: { tv: { address: 1.2.3.4, inputs: { a: [left] } } } }", "library.android.tv.inputs.a")]
    [InlineData("library: { android: { tv: { address: 1.2.3.4, inputs: { a: [up, ~] } } } }", "library.android.tv.inputs.a")]
    [InlineData("library: { android: { tv: { address: 1.2.3.4, inputs: { a: { b: up } } } } }", "library.android.tv.inputs.a")]
    [InlineData("library: { android: { tv: { address: 1.2.3.4, inputs: hdmi2 } } }", "library.android.tv.inputs")]
    public void BadSettingsAndLibraryEntriesAreFileWideProblems(string yaml, string path)
    {
        var load = ConfigYaml.Load(yaml);

        var problem = Assert.Single(load.Problems);
        Assert.Equal(path, problem.Path);
        Assert.Null(problem.FlowId);
        Assert.NotNull(load.Config);
    }

    [Fact]
    public void EnumsReadCaseInsensitivelyAndSaveCamelCase()
    {
        var config = LoadClean("settings: { popup: { position: bottomleft } }");

        Assert.Equal(PopupPosition.BottomLeft, config.Settings.Popup.Position);
        Assert.Contains("position: bottomLeft", ConfigYaml.Save(config), StringComparison.Ordinal);
    }

    [Fact]
    public void AudioEntryWithBadFlowIsDroppedSoItsReferencesFail()
    {
        var load = ConfigYaml.Load("library: { audio: { mic: { match: Yeti, flow: inpt }, spk: { match: Dell } } }");

        Assert.Equal("library.audio.mic.flow", Assert.Single(load.Problems).Path);
        Assert.Equal(["spk"], load.Config!.Library.Audio.Select(d => d.Key));
    }

    [Fact]
    public void UnknownKeyInAfterIsIgnoredLikeOtherUnknownKeys()
    {
        var config = LoadClean("flows: { f: { nodes: [{ id: b, step: wait, after: [{ id: a, whne: { x: 1 } }] }] } }");

        Assert.Equal([new Edge("a")], config.Flows[0].Nodes[0].After);
    }

    [Theory]
    [InlineData("[{ id: a, when: [failure] }]")]
    [InlineData("[{ id: [a] }]")]
    [InlineData("[[a]]")]
    public void WrongShapeInAfterIsAProblemOnItsFlowOnly(string after)
    {
        var load = ConfigYaml.Load($$"""
            flows:
              bad: { nodes: [{ id: b, step: wait, after: {{after}} }] }
              good: { nodes: [{ id: a, step: wait }] }
            """);

        var problem = Assert.Single(load.Problems);
        Assert.Equal(("bad", "flows.bad.nodes.b.after"), (problem.FlowId, problem.Path));
        Assert.Empty(load.Config!.Flows[0].Nodes[0].After);
        Assert.Equal(2, load.Config.Flows.Count);
    }

    [Theory]
    [InlineData("flows: { a: { nodes: [] }, a: { nodes: [] } }")]
    [InlineData("x: &n { id: a, step: wait }\nflows: { f: { nodes: [{ id: a, step: wait, with: *n }] } }")]
    public void LoadNeverThrows(string yaml)
    {
        var load = ConfigYaml.Load(yaml);

        Assert.True(load.Config is not null || load.Problems.Count > 0);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAA==")]
    public void DamagedDisplayLayoutIsDroppedWithAProblem(string ccd)
    {
        var load = ConfigYaml.Load($"library: {{ displays: {{ desk: {{ monitors: [m], ccd: '{ccd}' }} }} }}");

        Assert.Empty(load.Config!.Library.Displays);
        Assert.Equal("library.displays.desk", Assert.Single(load.Problems).Path);
    }

    [Fact]
    public void WellFormedLayoutNeedsOneMonitorPerPath()
    {
        Assert.True(new DisplayLayout("d", ["m"], "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=").IsWellFormed());
        Assert.False(new DisplayLayout("d", ["m", "n"], "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=").IsWellFormed());
        Assert.False(new DisplayLayout("d", [" "], "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=").IsWellFormed());
    }

    [Fact]
    public void AndroidEntryReadsItsInputs()
    {
        var config = LoadClean("library: { android: { tv: { address: 192.168.1.50, inputs: { hdmi2: [down, down] } } } }");

        var tv = Assert.Single(config.Library.Android);
        Assert.Equal("192.168.1.50", tv.Address);
        Assert.Null(tv.Adb);
        Assert.Equal(["down", "down"], tv.Inputs["hdmi2"]);
        Assert.Contains("android:", ConfigYaml.Save(config), StringComparison.Ordinal);
    }

    [Fact]
    public void LoneAndroidMoveIsAOneMoveList() =>
        Assert.Equal(["down"], LoadClean("library: { android: { tv: { address: 1.2.3.4, inputs: { hdmi2: down } } } }").Library.Android[0].Inputs["hdmi2"]);

    [Fact]
    public void OldFiretvSectionPointsAtAndroid()
    {
        var load = ConfigYaml.Load("library: { firetv: { tv: { address: 1.2.3.4 } } }");

        var problem = Assert.Single(load.Problems);
        Assert.Equal("library.firetv", problem.Path);
        Assert.Contains("android:", problem.Message, StringComparison.Ordinal);
        Assert.Empty(load.Config!.Library.Android);
        Assert.DoesNotContain("firetv", ConfigYaml.Save(load.Config), StringComparison.Ordinal);
    }
}
