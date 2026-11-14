using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Input;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>M12 core: <c>menu.show</c> (flow-menu/DESIGN.md §2.1, §2.4, §2.5, §3.5).</summary>
public class FlowMenuTests
{
    private const string Couch = """
        flows:
          couch:
            name: Couch
            glyph: E7FC
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

        var again = Load(ConfigYaml.Save(config));
        Assert.Equal(config, again);
        Assert.Empty(ConfigValidator.Validate(config));
        Assert.Equal(["couch-tv", "desk", "connect-technics", "tv-sleep"], ConfigValidator.MenuItems((StepNode)couch.Nodes[0]));
    }

    [Fact]
    public void AGamepadKeyFromV040LoadsWithOneWarningAndIsDroppedOnSave()
    {
        var yaml = Couch.Replace("glyph: E7FC", "glyph: E7FC\n    gamepad: [View+Menu]") + "\n" + Others;
        var load = ConfigYaml.Load(yaml);
        var config = load.Config!;

        var warning = Assert.Single(load.Problems);
        Assert.Equal((Severity.Warning, "couch", "flows.couch.gamepad"), (warning.Severity, warning.FlowId, warning.Path));
        Assert.Contains("removed", warning.Message);
        Assert.Empty(ConfigValidator.Validate(config));
        Assert.DoesNotContain("gamepad", ConfigYaml.Save(config));
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
    public void AnEmptyItemIsAWarning() =>
        Assert.Contains(Validate(Menu("items: [a, \"\"]")), p => p.Severity == Severity.Warning && p.Message.Contains("empty entry"));

    [Fact]
    public void AMenuErrorMakesCallersSayTheFlowNeedsFixing()
    {
        var problems = Validate("""
            flows:
              a: { name: A, nodes: [ { id: c, step: flow.run, with: { flow: b } } ] }
              b:
                name: B
                nodes:
                  - { id: pick, step: menu.show, with: { items: [b] } }
            """);

        Assert.Contains(problems, p => p.FlowId == "a" && p.Severity == Severity.Warning && p.Message.Contains("needs fixing"));
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
