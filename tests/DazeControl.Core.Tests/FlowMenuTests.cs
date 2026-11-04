using DazeControl.Core.Catalog;
using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>M12 core: <c>menu.show</c>, controller chords, <c>gamepad:</c> (flow-menu/DESIGN.md §2.1, §2.4, §2.5, §3.5).</summary>
public class FlowMenuTests
{
    private const string Couch = """
        flows:
          couch:
            name: Couch
            glyph: E7FC
            gamepad: [View+Menu]
            whenRunning: ignore
            nodes:
              - id: pick
                step: menu.show
                with:
                  items: [couch-tv, desk, connect-technics, tv-sleep]
                  title: Couch
                  layout: list
                  size: large
                  closeAfter: 30s
                  wait: false
        """;

    private const string Others = """
          couch-tv: { name: Couch TV, nodes: [ { id: n, step: notify, with: { text: x } } ] }
          desk: { name: Desk, nodes: [ { id: n, step: notify, with: { text: x } } ] }
          connect-technics: { name: Connect Technics, nodes: [ { id: n, step: notify, with: { text: x } } ] }
          tv-sleep: { name: TV sleep, nodes: [ { id: n, step: notify, with: { text: x } } ] }
        """;

    private static Configuration Load(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private static ValueList<Problem> Validate(string yaml) => ConfigValidator.Validate(Load(yaml));

    /// <summary>One menu flow "m" with the given <c>with:</c> body, next to flows a, b and c.</summary>
    private static string Menu(string with, string extra = "") => $$"""
        flows:
          m:
            name: M
            {{extra}}
            nodes:
              - { id: pick, step: menu.show, with: { {{with}} } }
          a: { name: A, nodes: [ { id: n, step: notify, with: { text: x } } ] }
          b: { name: B, nodes: [ { id: n, step: notify, with: { text: x } } ] }
        """;

    [Fact]
    public void TheDesignFlowRoundTripsAndIsValid()
    {
        var yaml = Couch + "\n" + Others;
        var config = Load(yaml);
        var couch = config.Flows[0];

        Assert.Equal(["View+Menu"], couch.Gamepad);
        var again = Load(ConfigYaml.Save(config));
        Assert.Equal(config, again);
        Assert.Empty(ConfigValidator.Validate(config));
        Assert.Equal(["couch-tv", "desk", "connect-technics", "tv-sleep"], ConfigValidator.MenuItems((StepNode)couch.Nodes[0]));
    }

    [Theory]
    [InlineData("View+Menu", "View+Menu")]
    [InlineData("menu + view", "View+Menu")]
    [InlineData("Y+RB+LB", "Y+LB+RB")]
    [InlineData("up+down+Left+RIGHT", "Up+Down+Left+Right")]
    public void AChordIsWrittenInTheCanonicalOrder(string written, string canonical)
    {
        Assert.True(GamepadChord.TryParse(written, out var chord));
        Assert.Equal(canonical, chord.ToString());
        Assert.Equal(chord, GamepadChord.TryParse(canonical, out var same) ? same : null);
    }

    [Fact]
    public void ADisplayChordHasSpacesAroundThePlus() =>
        Assert.Equal("View + Menu", new GamepadChord(GamepadButtons.Menu | GamepadButtons.View).Display);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Guide")]
    [InlineData("Guide+A")]
    [InlineData("A+B+X+Y+LB")]
    [InlineData("A+A")]
    [InlineData("A+Select")]
    [InlineData("A++B")]
    public void ChordsThatAreRefused(string? text)
    {
        Assert.False(GamepadChord.TryParse(text, out var chord));
        Assert.Null(chord);
        Assert.NotNull(GamepadChord.Explain(text));
    }

    [Fact]
    public void FourButtonsAreTheMostAndGuideSaysWhy()
    {
        Assert.True(GamepadChord.TryParse("A+B+X+Y", out _));
        Assert.Contains("Game Bar", GamepadChord.Explain("Guide+A"));
    }

    [Fact]
    public void ABadChordIsAnErrorOnTheFlowNotALoadFailure()
    {
        var problems = Validate(Menu("items: [a]", "gamepad: [Guide+A, View]"));

        Assert.Equal(["flows.m.gamepad", "flows.m.gamepad"], problems.Select(p => p.Path));
        Assert.All(problems, p => Assert.Equal((Severity.Error, "m"), (p.Severity, p.FlowId)));
    }

    [Fact]
    public void ALaterFlowWithTheSameChordGetsTheError()
    {
        var problems = Validate("""
            flows:
              first: { name: First, gamepad: [View+Menu], nodes: [ { id: n, step: notify, with: { text: x } } ] }
              second: { name: Second, gamepad: ["Menu + View"], nodes: [ { id: n, step: notify, with: { text: x } } ] }
              off: { name: Off, enabled: false, gamepad: [A+B], nodes: [ { id: n, step: notify, with: { text: x } } ] }
              third: { name: Third, gamepad: [A+B], nodes: [ { id: n, step: notify, with: { text: x } } ] }
            """);

        var problem = Assert.Single(problems);
        Assert.Equal(("second", "flows.second.gamepad", "View + Menu already runs flow \"first\"."), (problem.FlowId, problem.Path, problem.Message));
    }

    [Fact]
    public void AMenuCantListItsOwnFlow()
    {
        var problem = Assert.Single(Validate(Menu("items: [a, m]")));

        Assert.Equal((Severity.Error, "flows.m.nodes.pick.with.items", "A menu can't list its own flow.", "m"),
            (problem.Severity, problem.Path, problem.Message, problem.FlowId));
    }

    [Fact]
    public void AnUnknownItemIsAWarning()
    {
        var problem = Assert.Single(Validate(Menu("items: [a, gone]")));

        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Contains("gone isn't there anymore", problem.Message);
    }

    [Fact]
    public void ADuplicateItemIsAWarning()
    {
        var problem = Assert.Single(Validate(Menu("items: [a, b, a]")));

        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Contains("a is listed twice", problem.Message);
    }

    [Fact]
    public void ZeroOrThirteenItemsIsAnError()
    {
        var thirteen = string.Join(", ", Enumerable.Repeat("a", 13));
        var twelve = string.Join(", ", Enumerable.Repeat("a", 12));

        Assert.Equal(Severity.Error, Assert.Single(Validate(Menu("items: []")), p => p.Severity == Severity.Error).Severity);
        Assert.Contains(Validate(Menu($"items: [{thirteen}]")), p => p.Severity == Severity.Error && p.Message.Contains("1 to 12"));
        Assert.DoesNotContain(Validate(Menu($"items: [{twelve}]")), p => p.Severity == Severity.Error);
    }

    [Theory]
    [InlineData("4s", true)]
    [InlineData("5s", false)]
    [InlineData("10m", false)]
    [InlineData("601s", true)]
    public void CloseAfterIsFiveSecondsToTenMinutes(string value, bool error)
    {
        var problems = Validate(Menu($"items: [a], closeAfter: {value}", ""));

        Assert.Equal(error, problems.Any(p => p.Severity == Severity.Error && p.Path.EndsWith(".closeAfter", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheStepTimeoutMustBeLongerThanCloseAfter()
    {
        string Yaml(string timeout) => $$"""
            flows:
              m:
                name: M
                nodes:
                  - { id: pick, step: menu.show, timeout: {{timeout}}, with: { items: [a], closeAfter: 1m } }
              a: { name: A, nodes: [ { id: n, step: notify, with: { text: x } } ] }
            """;

        Assert.Contains(Validate(Yaml("1m")), p => p.Path == "flows.m.nodes.pick.timeout");
        Assert.Contains(Validate(Yaml("30s")), p => p.Path == "flows.m.nodes.pick.timeout");
        Assert.Empty(Validate(Yaml("90s")));
    }

    [Fact]
    public void AWaitingMenuThatReachesBackToItsFlowIsALoop()
    {
        var problems = Validate("""
            flows:
              m:
                name: M
                nodes:
                  - { id: pick, step: menu.show, with: { items: [a], wait: true } }
              a:
                name: A
                nodes:
                  - { id: run, step: flow.run, with: { flow: m } }
            """);

        Assert.Contains(problems, p => p.Severity == Severity.Error && p.FlowId == "m" && p.Path == "flows.m.nodes.pick.with.items"
            && p.Message == "That would make a loop: M runs A, which runs M.");
        Assert.Contains(problems, p => p.Severity == Severity.Error && p.FlowId == "a");
    }

    [Fact]
    public void ALaunchOnlyMenuMayPointBackAtItsParent()
    {
        var problems = Validate("""
            flows:
              m:
                name: M
                nodes:
                  - { id: pick, step: menu.show, with: { items: [sub] } }
              sub:
                name: Sub
                nodes:
                  - { id: pick, step: menu.show, with: { items: [m] } }
            """);

        Assert.Empty(problems);
    }

    [Fact]
    public void WaitingMenusThroughMenusAreALoopToo()
    {
        var problems = Validate("""
            flows:
              m:
                name: M
                nodes:
                  - { id: pick, step: menu.show, with: { items: [sub], wait: true } }
              sub:
                name: Sub
                nodes:
                  - { id: pick, step: menu.show, with: { items: [m], wait: true } }
            """);

        Assert.Equal(["m", "sub"], ConfigValidator.FlowsWithErrors(problems).Order());
    }

    [Fact]
    public void CallProblemSeesWaitingMenus()
    {
        var config = Load("""
            flows:
              m:
                name: M
                nodes:
                  - { id: pick, step: menu.show, with: { items: [a], wait: true } }
              a: { name: A, nodes: [ { id: n, step: notify, with: { text: x } } ] }
            """);

        Assert.NotNull(ConfigValidator.CallProblem(config.Flows, "a", "m"));
        Assert.Null(ConfigValidator.CallProblem(config.Flows, "m", "a"));
    }

    [Fact]
    public void RenamingAFlowRewritesMenuItemsAndCallers()
    {
        var config = Load(Couch + "\n" + Others);

        var renamed = FlowRefs.Rename(config, "desk", "workbench");

        var step = (StepNode)renamed.Flows[0].Nodes[0];
        Assert.Equal(["couch-tv", "workbench", "connect-technics", "tv-sleep"], ConfigValidator.MenuItems(step));
        Assert.Equal(["couch"], FlowRefs.Callers(config, "desk").Select(f => f.Id));
        Assert.Same(config, FlowRefs.Rename(config, "nope", "x"));
    }

    [Fact]
    public void TheCatalogHasTheMenuStepWithItsItemsSuggestedAndOrdered()
    {
        var items = ProviderCatalog.Find(ProviderCatalog.MenuShowId)!.FindParam("items")!;

        Assert.Equal(ConfigRef.Flow, items.RefersTo);
        Assert.True(items.Ordered);
        Assert.Equal(ParamSuggest.Config(ParamSuggest.Flows), items.Suggest);
    }
}
