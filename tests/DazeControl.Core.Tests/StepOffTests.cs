using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>A step turned off (<c>enabled: false</c>, T25.4, D-148): YAML, validation and "n of m".</summary>
public class StepOffTests
{
    private const string Header = """
        library:
          audio:
            technics: { match: Technics }
          bluetooth:
            buds: { address: "B8:20:8E:ED:48:FC", name: Buds }
        flows:
          f:
            name: F
            nodes:

        """;

    private static ConfigLoad LoadRaw(params string[] nodes) =>
        ConfigYaml.Load(Header + string.Concat(nodes.Select(n => $"      - {n}\n")));

    private static Configuration Load(params string[] nodes)
    {
        var load = LoadRaw(nodes);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    [Fact]
    public void EnabledFalseLoadsAndSavesBack()
    {
        var config = Load("{ id: bt, step: bluetooth.connect, with: { device: buds }, enabled: false }",
            "{ id: note, step: notify, with: { text: Hi }, after: [bt] }");

        var steps = config.Flows[0].Nodes.OfType<StepNode>().ToList();
        Assert.Equal([false, true], steps.Select(s => s.Enabled));
        var saved = ConfigYaml.Save(config);
        Assert.Single(saved.Split('\n'), l => l.Contains("enabled: false", StringComparison.Ordinal));
        Assert.Equal(config, ConfigYaml.Load(saved).Config);
    }

    [Fact]
    public void AStepThatIsOnWritesNothingNew()
    {
        var config = Load("{ id: note, step: notify, with: { text: Hi } }");

        Assert.DoesNotContain("enabled", ConfigYaml.Save(config), StringComparison.Ordinal);
    }

    [Fact]
    public void ACardCantBeTurnedOff()
    {
        var load = LoadRaw("{ id: a, step: notify, with: { text: Hi } }", "{ id: card, group: [a], enabled: false }");

        Assert.Contains(load.Problems, p => p.Message.Contains("enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonBooleanIsAProblem()
    {
        var load = LoadRaw("{ id: a, step: notify, with: { text: Hi }, enabled: maybe }");

        Assert.Contains(load.Problems, p => p.Message == "\"maybe\" isn't true or false.");
    }

    [Fact]
    public void AValueFromAnOffStepWarnsUnlessItHasAFallback()
    {
        var problem = Assert.Single(ConfigValidator.Validate(Load(
            "{ id: bt, step: bluetooth.connect, with: { device: buds }, enabled: false }",
            "{ id: note, step: notify, with: { text: \"On {bt.device}\" }, after: [bt] }")));
        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.StartsWith("\"Connect\" is turned off, so Device is empty", problem.Message, StringComparison.Ordinal);

        Assert.Empty(ConfigValidator.Validate(Load(
            "{ id: bt, step: bluetooth.connect, with: { device: buds }, enabled: false }",
            "{ id: note, step: notify, with: { text: \"On {bt.device|nothing}\" }, after: [bt] }")));
    }

    [Fact]
    public void ADeviceFromAnOffStepWarnsThatTheStepFails()
    {
        var problem = Assert.Single(ConfigValidator.Validate(Load(
            "{ id: set, step: audio.setDefault, with: { device: technics }, enabled: false }",
            "{ id: back, step: audio.setDefault, with: { device: \"$result:set.previous\" }, after: [set] }")));

        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.EndsWith("is turned off, so this step has no device and fails.", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenDeviceReferenceToAnOffStepKeepsOnlyItsError()
    {
        var problem = Assert.Single(ConfigValidator.Validate(Load(
            "{ id: set, step: audio.setDefault, with: { device: technics }, enabled: false }",
            "{ id: back, step: audio.setDefault, with: { device: \"$result:set.nmae\" }, after: [set] }")));

        Assert.Equal(Severity.Error, problem.Severity);
    }

    [Fact]
    public void AStepAfterAnOffStopFlowIsNotWarnedAbout()
    {
        Assert.Empty(ConfigValidator.Validate(Load(
            "{ id: stop, step: flow.stop, enabled: false }",
            "{ id: note, step: notify, with: { text: Hi }, after: [stop] }")));
    }

    [Fact]
    public void NOfMLeavesOffStepsOut()
    {
        var progress = new RunProgress(Load(
            "{ id: a, step: notify, with: { text: Hi } }",
            "{ id: b, step: notify, with: { text: Hi }, enabled: false, after: [a] }").Flows[0]);

        Assert.Equal(1, progress.Total);
        Assert.False(progress.Counts("b"));
    }
}
