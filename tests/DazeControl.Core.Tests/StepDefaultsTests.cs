using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

public class StepDefaultsTests
{
    private const string Sample = """
        version: 1
        settings:
          stepDefaults:
            audio.setDefault:   { timeout: 1s, retry: { count: 2, delay: 500ms } }
            bluetooth.connect:  { timeout: 30s, onFailure: skip }
            android.input:      { retry: { count: 1 } }
        flows:
          f:
            nodes:
              - { id: a, step: audio.setDefault, with: { device: x } }
        """;

    private static ProviderDescriptor Audio => ProviderCatalog.Find("audio.setDefault")!;

    private static Configuration Load(string yaml, int problems = 0)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Equal(problems, load.Problems.Count);
        return load.Config!;
    }

    private static StepNode Step(Func<StepNode, StepNode>? set = null) => (set ?? (s => s))(new StepNode("a", "audio.setDefault"));

    [Fact]
    public void BuiltInWinsWhenNothingElseSays()
    {
        var r = StepSettings.Resolve(Step(), Audio, null);
        Assert.Equal(Audio.DefaultTimeout, r.Timeout);
        Assert.Equal(0, r.RetryCount);
        Assert.Equal(StepSettings.DefaultRetryDelay, r.RetryDelay);
        Assert.Equal(FailurePolicy.Stop, r.OnFailure);
    }

    [Fact]
    public void TypeDefaultBeatsBuiltIn()
    {
        var d = new StepDefault { Timeout = TimeSpan.FromSeconds(1), RetryCount = 2, RetryDelay = TimeSpan.FromMilliseconds(500), OnFailure = FailurePolicy.Skip };
        var r = StepSettings.Resolve(Step(), Audio, d);
        Assert.Equal(new StepSettings(TimeSpan.FromSeconds(1), 2, TimeSpan.FromMilliseconds(500), FailurePolicy.Skip), r);
    }

    [Fact]
    public void OwnValueBeatsTypeDefaultPerFieldAndAnExplicitStopOverridesSkip()
    {
        var d = new StepDefault { Timeout = TimeSpan.FromSeconds(1), RetryCount = 2, RetryDelay = TimeSpan.FromMilliseconds(500), OnFailure = FailurePolicy.Skip };
        var step = Step(s => s with { Timeout = TimeSpan.FromSeconds(9), RetryCount = 5, OnFailure = FailurePolicy.Stop });
        var r = StepSettings.Resolve(step, Audio, d);
        Assert.Equal(TimeSpan.FromSeconds(9), r.Timeout);
        Assert.Equal(5, r.RetryCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), r.RetryDelay); // count set, delay inherited
        Assert.Equal(FailurePolicy.Stop, r.OnFailure);
    }

    [Fact]
    public void AnOwnZeroRetryCountBeatsADefaultCount()
    {
        var r = StepSettings.Resolve(Step(s => s with { RetryCount = 0 }), Audio, new StepDefault { RetryCount = 3 });
        Assert.Equal(0, r.RetryCount);
    }

    [Fact]
    public void ApplyFillsStepsAndCardsAndLeavesOwnValues()
    {
        var defaults = new ValueDictionary<StepDefault>([KeyValuePair.Create("audio.setDefault", new StepDefault { Timeout = TimeSpan.FromSeconds(1), OnFailure = FailurePolicy.Skip })]);
        var flow = new Flow("f", "F", [Step(), new StepNode("b", "audio.setDefault") { Timeout = TimeSpan.FromSeconds(7) }, new GroupNode("g", ["a"])]);
        var applied = StepDefaults.Apply(flow, defaults);
        var a = (StepNode)applied.FindNode("a")!;
        Assert.Equal(TimeSpan.FromSeconds(1), a.Timeout);
        Assert.Equal(FailurePolicy.Skip, a.OnFailure);
        Assert.Equal(0, a.RetryCount);
        Assert.Equal(TimeSpan.FromSeconds(7), ((StepNode)applied.FindNode("b")!).Timeout);
        Assert.Equal(FailurePolicy.Stop, applied.FindNode("g")!.OnFailure);
        Assert.Equal(applied, StepDefaults.Apply(applied, defaults)); // idempotent
    }

    [Fact]
    public void SampleRoundTrips()
    {
        var config = Load(Sample);
        var defaults = config.Settings.StepDefaults;
        Assert.Equal(3, defaults.Count);
        Assert.Equal(new StepDefault { Timeout = TimeSpan.FromSeconds(1), RetryCount = 2, RetryDelay = TimeSpan.FromMilliseconds(500) }, defaults["audio.setDefault"]);
        Assert.Equal(new StepDefault { RetryCount = 1 }, defaults["android.input"]); // delay omitted = inherits
        Assert.Equal(FailurePolicy.Skip, defaults["bluetooth.connect"].OnFailure);
        var saved = ConfigYaml.Save(config);
        Assert.DoesNotContain("popup", saved, StringComparison.Ordinal); // a default pop-up is not written
        var again = Load(saved);
        Assert.Equal(config.Settings, again.Settings);
        Assert.Equal(saved, ConfigYaml.Save(again));
    }

    [Fact]
    public void ExplicitStopIsKeptAndNullIsOmitted()
    {
        var config = Load("""
            flows:
              f:
                nodes:
                  - { id: a, step: audio.setDefault, with: { device: x }, onFailure: stop }
                  - { id: b, step: audio.setDefault, with: { device: x }, after: [a] }
            """);
        var nodes = config.Flows[0].Nodes;
        Assert.Equal(FailurePolicy.Stop, nodes[0].OnFailure);
        Assert.Null(nodes[1].OnFailure);
        var saved = ConfigYaml.Save(config);
        Assert.Equal(2, saved.Split("onFailure", StringSplitOptions.None).Length); // exactly one
        Assert.Contains("onFailure: stop", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryDelayAloneInheritsTheCount()
    {
        var config = Load("""
            flows:
              f:
                nodes:
                  - { id: a, step: audio.setDefault, with: { device: x }, retry: { delay: 2s } }
            """);
        var step = (StepNode)config.Flows[0].Nodes[0];
        Assert.Null(step.RetryCount);
        Assert.Equal(TimeSpan.FromSeconds(2), step.RetryDelay);
    }

    [Theory]
    [InlineData("xyz: { timeout: 1s }", "settings.stepDefaults.xyz")]
    [InlineData("group: { timeout: 1s }", "settings.stepDefaults.group")]
    [InlineData("wait: { timeout: 0s }", "settings.stepDefaults.wait.timeout")]
    [InlineData("wait: { timeout: 25h }", "settings.stepDefaults.wait.timeout")]
    [InlineData("wait: { retry: { count: 11 } }", "settings.stepDefaults.wait.retry.count")]
    [InlineData("wait: { retry: { count: 1, delay: -1s } }", "settings.stepDefaults.wait.retry.delay")]
    [InlineData("wait: { onFailure: nope }", "settings.stepDefaults.wait.onFailure")]
    public void BadEntriesBecomeProblemsAndTheBadPartIsDropped(string entry, string path)
    {
        var load = ConfigYaml.Load($"settings:\n  stepDefaults:\n    {entry}\n");
        var problem = Assert.Single(load.Problems);
        Assert.Equal(path, problem.Path);
        Assert.Null(problem.FlowId); // never disables a flow
        Assert.DoesNotContain(load.Config!.Settings.StepDefaults, d => d.Value.Timeout is { } t && (t <= TimeSpan.Zero || t > ConfigValidator.MaxDuration));
        Assert.DoesNotContain(load.Config.Settings.StepDefaults, d => d.Value.RetryCount > ConfigValidator.MaxRetries || d.Value.RetryDelay < TimeSpan.Zero);
        Assert.DoesNotContain("xyz", load.Config.Settings.StepDefaults.Keys);
        Assert.DoesNotContain("group", load.Config.Settings.StepDefaults.Keys);
    }

    [Fact]
    public void ABadFieldKeepsTheRestOfTheEntry()
    {
        var load = ConfigYaml.Load("settings:\n  stepDefaults:\n    wait: { timeout: 0s, onFailure: skip, retry: { count: 2 } }\n");
        Assert.Single(load.Problems);
        Assert.Equal(new StepDefault { OnFailure = FailurePolicy.Skip, RetryCount = 2 }, load.Config!.Settings.StepDefaults["wait"]);
    }

    [Fact]
    public void DefaultTimeoutShorterThanMenuCloseAfterIsTheFlowError()
    {
        var config = Load("""
            settings:
              stepDefaults:
                menu.show: { timeout: 10s }
            flows:
              m:
                nodes:
                  - { id: a, step: menu.show, with: { items: [m2] } }
              m2:
                nodes:
                  - { id: b, step: wait, with: { duration: 1s } }
            """);
        var problems = ConfigValidator.Validate(config);
        Assert.Contains(problems, p => p.FlowId == "m" && p.Path.EndsWith(".timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void ADefaultTimeoutShorterThanAWaitIsTheFlowError()
    {
        var config = Load("""
            settings:
              stepDefaults:
                wait: { timeout: 1s }
            flows:
              f:
                nodes:
                  - { id: a, step: wait, with: { duration: 2s } }
            """);
        Assert.Contains(ConfigValidator.Validate(config), p => p.FlowId == "f" && p.Path == "flows.f.nodes.a.timeout");
    }

    [Fact]
    public void AMenuCloseAfterAtTheBuiltInTimeoutIsTheFlowError()
    {
        var config = Load("""
            flows:
              m:
                nodes:
                  - { id: a, step: menu.show, with: { items: [m2], closeAfter: 10m } }
              m2:
                nodes:
                  - { id: b, step: wait, with: { duration: 1s } }
            """);
        Assert.Contains(ConfigValidator.Validate(config), p => p.FlowId == "m" && p.Path.EndsWith(".timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void ANullEntryIsReported()
    {
        var load = ConfigYaml.Load("settings:\n  stepDefaults:\n    wait:\n");
        Assert.Equal("settings.stepDefaults.wait", Assert.Single(load.Problems).Path);
    }

    [Theory]
    [InlineData("full.yaml")]
    [InlineData("samples/user-config.yaml")]
    public void ExistingFilesSaveWithoutNewKeysAndStayStable(string name)
    {
        var yaml = name == "full.yaml" ? ConfigYamlTests.Fixture(name) : File.ReadAllText(RepoFile(name));
        var load = ConfigYaml.Load(yaml);
        var saved = ConfigYaml.Save(load.Config!);
        Assert.DoesNotContain("stepDefaults", saved, StringComparison.Ordinal);
        var reloaded = ConfigYaml.Load(saved);
        Assert.Equal(load.Config, reloaded.Config);
        Assert.Equal(saved, ConfigYaml.Save(reloaded.Config!)); // later saves rewrite nothing
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void OnlyResolvesOwnThenTypeDefaultAndAlwaysOverridesIt()
    {
        var d = new StepDefault { Only = "disconnected" };
        var connect = new StepNode("a", "bluetooth.connect");
        var descriptor = ProviderCatalog.Find("bluetooth.connect");
        Assert.Equal("disconnected", StepSettings.Resolve(connect, descriptor, d).Only);
        Assert.Equal("always", StepSettings.Resolve(connect with { Only = "always" }, descriptor, d).Only);
        Assert.Null(StepSettings.Resolve(connect, descriptor, null).Only); // built-in: always, no existing config changes
        Assert.Null(StepSettings.Resolve(connect, descriptor, new StepDefault { Timeout = TimeSpan.FromSeconds(5) }).Only);
        var flow = new Flow("f", "F", [connect, connect with { Id = "b", Only = "always" }]);
        var applied = StepDefaults.Apply(flow, new(new[] { KeyValuePair.Create("bluetooth.connect", d) }));
        Assert.Equal(["disconnected", "always"], applied.Nodes.Cast<StepNode>().Select(n => n.Only));
    }

    [Fact]
    public void OnlyInStepDefaultsRoundTripsAndABadIdIsAProblemAndDropped()
    {
        var config = Load("""
            settings:
              stepDefaults:
                bluetooth.connect: { only: disconnected }
                bluetooth.disconnect: { only: always }
            """);
        Assert.Equal("disconnected", config.Settings.StepDefaults["bluetooth.connect"].Only);
        var again = Load(ConfigYaml.Save(config));
        Assert.Equal("disconnected", again.Settings.StepDefaults["bluetooth.connect"].Only);
        Assert.Equal("always", again.Settings.StepDefaults["bluetooth.disconnect"].Only);

        var load = ConfigYaml.Load("""
            settings:
              stepDefaults:
                bluetooth.connect: { only: connected, timeout: 5s }
                wait: { only: disconnected }
            """);
        Assert.Equal(["settings.stepDefaults.bluetooth.connect.only", "settings.stepDefaults.wait.only"], load.Problems.Select(p => p.Path));
        Assert.All(load.Problems, p => Assert.Null(p.FlowId));
        Assert.Null(load.Config!.Settings.StepDefaults["bluetooth.connect"].Only);
        Assert.DoesNotContain("wait", load.Config.Settings.StepDefaults.Keys);
    }
}
