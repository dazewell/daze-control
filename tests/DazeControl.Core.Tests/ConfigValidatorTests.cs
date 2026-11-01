using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

public class ConfigValidatorTests
{
    private const string Library = """
        library:
          audio: { technics: { match: Technics } }
          bluetooth: { technics: { address: "B8:20:8E:ED:48:FC" } }

        """;

    private static ValueList<Problem> Validate(string yaml)
    {
        var load = ConfigYaml.Load(Library + yaml);
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    /// <summary>One flow "f" with these nodes (YAML flow-style lines).</summary>
    private static ValueList<Problem> ValidateNodes(params string[] nodes) =>
        Validate("flows:\n  f:\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n")));

    private static Problem Single(ValueList<Problem> problems, string path)
    {
        var problem = Assert.Single(problems);
        Assert.Equal(path, problem.Path);
        return problem;
    }

    [Fact]
    public void FullFixtureIsValid()
    {
        var load = ConfigYaml.Load(ConfigYamlTests.Fixture("full.yaml"));

        Assert.Empty(ConfigValidator.Validate(load.Config!));
    }

    [Fact]
    public void ErrorsInAFlowCarryItsId()
    {
        var problems = Validate("""
            flows:
              good: { nodes: [{ id: a, step: wait, with: { duration: 1s } }] }
              bad: { nodes: [{ id: a, step: nope }] }
            """);

        Assert.Equal("bad", Single(problems, "flows.bad.nodes.a.step").FlowId);
        Assert.Equal(["bad"], ConfigValidator.FlowsWithErrors(problems));
    }

    [Fact]
    public void EmptyFlowIsOnlyAWarning()
    {
        var problems = Validate("flows: { f: { nodes: [] } }");

        Assert.Equal(Severity.Warning, Single(problems, "flows.f.nodes").Severity);
        Assert.Empty(ConfigValidator.FlowsWithErrors(problems));
    }

    [Fact]
    public void DuplicateNodeIds() =>
        Single(ValidateNodes("{ id: a, step: notify, with: { text: x } }", "{ id: a, step: notify, with: { text: y } }"), "flows.f.nodes.a");

    [Theory]
    [InlineData("[ghost]", "No step or card called \"ghost\"")]
    [InlineData("[b]", "can't wait for itself")]
    [InlineData("[a, a]", "listed twice")]
    public void BadEdges(string after, string message)
    {
        var problem = Single(ValidateNodes("{ id: a, step: notify, with: { text: x } }",
            $"{{ id: b, step: notify, with: {{ text: x }}, after: {after} }}"), "flows.f.nodes.b.after");

        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CycleIsReportedOnceWithItsPath()
    {
        var problem = Single(ValidateNodes(
            "{ id: a, step: notify, with: { text: x }, after: [c] }",
            "{ id: b, step: notify, with: { text: x }, after: [a] }",
            "{ id: c, step: notify, with: { text: x }, after: [b] }",
            "{ id: d, step: notify, with: { text: x }, after: [c] }"), "flows.f.nodes.a.after");

        Assert.Contains("a → c → b → a", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiamondIsNotACycle() => Assert.Empty(ValidateNodes(
        "{ id: a, step: notify, with: { text: x } }",
        "{ id: b, step: notify, with: { text: x }, after: [a] }",
        "{ id: c, step: notify, with: { text: x }, after: [{ id: a, when: failure }] }",
        "{ id: d, step: notify, with: { text: x }, after: [b, { id: c, when: always }] }"));

    private const string StepA = "{ id: a, step: notify, with: { text: x } }";
    private const string StepB = "{ id: b, step: notify, with: { text: x } }";

    [Theory]
    [InlineData("{ id: g, group: [a] }", "flows.f.nodes.g.group", "at least two")]
    [InlineData("{ id: g, group: [a, ghost] }", "flows.f.nodes.g.group", "No step called \"ghost\"")]
    [InlineData("{ id: g, group: [a, a] }", "flows.f.nodes.g.group", "listed twice")]
    public void BadCards(string card, string path, string message)
    {
        var problems = ValidateNodes(StepA, StepB, card);

        Assert.Contains(problems, p => p.Path == path && p.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void CardsCantHoldCards()
    {
        var problems = ValidateNodes(StepA, StepB, "{ id: g, group: [a, b] }", "{ id: h, group: [g, a] }");

        Assert.Contains(problems, p => p.Path == "flows.f.nodes.h.group" && p.Message.Contains("cards can't hold cards", StringComparison.Ordinal));
    }

    [Fact]
    public void StepInTwoCards()
    {
        var problems = ValidateNodes(StepA, StepB, "{ id: c, step: notify, with: { text: x } }",
            "{ id: g, group: [a, b] }", "{ id: h, group: [b, c] }");

        Assert.Contains("already in card \"g\"", Single(problems, "flows.f.nodes.h.group").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEdgesIntoOrOutOfCardMembers()
    {
        var problems = ValidateNodes(
            StepA,
            "{ id: b, step: notify, with: { text: x }, after: [z] }",
            "{ id: z, step: notify, with: { text: x } }",
            "{ id: g, group: [a, b] }",
            "{ id: c, step: notify, with: { text: x }, after: [a] }");

        Assert.Equal(["flows.f.nodes.b.after", "flows.f.nodes.c.after"], problems.Select(p => p.Path));
    }

    [Fact]
    public void ValidCardWithEdgesOnTheCard() => Assert.Empty(ValidateNodes(
        StepA, StepB, "{ id: z, step: notify, with: { text: x } }",
        "{ id: g, group: [a, b], after: [z] }", "{ id: c, step: notify, with: { text: x }, after: [g] }"));

    [Theory]
    [InlineData("{ id: a, step: bluetooth.connect }", "flows.f.nodes.a.with", "Device is missing")]
    [InlineData("{ id: a, step: bluetooth.connect, with: { device: sony } }", "flows.f.nodes.a.with.device", "No Bluetooth device called \"sony\"")]
    [InlineData("{ id: a, step: audio.setDefault, with: { device: technics, roles: some } }", "flows.f.nodes.a.with.roles", "isn't one of")]
    [InlineData("{ id: a, step: audio.setVolume, with: { percent: 120 } }", "flows.f.nodes.a.with.percent", "Use 0 to 100")]
    [InlineData("{ id: a, step: audio.setVolume, with: { percent: loud } }", "flows.f.nodes.a.with.percent", "whole number")]
    [InlineData("{ id: a, step: audio.mute, with: { muted: yes } }", "flows.f.nodes.a.with.muted", "true or false")]
    [InlineData("{ id: a, step: wait, with: { duration: 5 } }", "flows.f.nodes.a.with.duration", "duration")]
    [InlineData("{ id: a, step: wait, with: { duration: 5s, extra: 1 } }", "flows.f.nodes.a.with.extra", "no setting \"extra\"")]
    [InlineData("{ id: a, step: process.run, with: { path: x.exe, args: -v } }", "flows.f.nodes.a.with.args", "list")]
    [InlineData("{ id: a, step: process.run, with: { path: [x.exe] } }", "flows.f.nodes.a.with.path", "single value")]
    [InlineData("{ id: a, step: process.run, with: { path: \"\" } }", "flows.f.nodes.a.with.path", "can't be empty")]
    [InlineData("{ id: a, step: http.request, with: { url: x, headers: [a] } }", "flows.f.nodes.a.with.headers", "name: value")]
    [InlineData("{ id: a, step: notify, with: { text: x }, timeout: 0s }", "flows.f.nodes.a.timeout", "more than 0")]
    [InlineData("{ id: a, step: notify, with: { text: x }, retry: { count: 11 } }", "flows.f.nodes.a.retry.count", "0 to 10")]
    public void ParamsAndTimingAreCheckedAgainstTheCatalog(string node, string path, string message)
    {
        var problem = Single(ValidateNodes(node), path);

        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalLibraryRefMayBeEmpty() =>
        Assert.Empty(ValidateNodes("{ id: a, step: audio.setVolume, with: { device: \"\", percent: 30 } }"));

    [Theory]
    [InlineData("[Z]", "isn't a shortcut")]
    [InlineData("[Alt+Z, alt+z]", "listed twice")]
    public void BadHotkeys(string hotkeys, string message)
    {
        var problem = Single(Validate($"flows: {{ f: {{ hotkeys: {hotkeys}, nodes: [{StepA}] }} }}"), "flows.f.hotkeys");

        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HotkeyTakenByAnotherFlowFailsTheLaterOne()
    {
        var problem = Single(Validate($$"""
            flows:
              first: { hotkeys: [Alt+Z], nodes: [{{StepA}}] }
              off: { enabled: false, hotkeys: [Alt+Z], nodes: [{{StepA}}] }
              second: { hotkeys: [Alt+Z], nodes: [{{StepA}}] }
            """), "flows.second.hotkeys");

        Assert.Contains("already runs flow \"first\"", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BadGlyphIsAWarning() =>
        Assert.Equal(Severity.Warning, Single(Validate($"flows: {{ f: {{ glyph: bluetooth, nodes: [{StepA}] }} }}"), "flows.f.glyph").Severity);

    [Fact]
    public void BadBluetoothAddressIsAFileWideError()
    {
        var problems = ConfigValidator.Validate(new Configuration
        {
            Library = new Library { Bluetooth = [new BluetoothDevice("x", "B8-20-8E-ED-48-FC")] },
        });

        Assert.Null(Single(problems, "library.bluetooth.x.address").FlowId);
    }
}
