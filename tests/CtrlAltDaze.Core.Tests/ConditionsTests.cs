using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>"Only if": YAML and validation (D-081, DESIGN B3/B7).</summary>
public sealed class ConditionsTests
{
    private static string Yaml(string step) => $$"""
        flows:
          f:
            nodes:
              - {{step}}
        """;

    private static readonly List<ProviderDescriptor> Catalog =
    [
        .. ProviderCatalog.All.Select(d => d.Id == "bluetooth.connect"
            ? d with { Conditions = [new ConditionSpec("disconnected", "It isn't connected", "bluetooth.connected", false)] }
            : d),
    ];

    private static IEnumerable<Problem> Errors(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        return load.Problems.Concat(ConfigValidator.Validate(load.Config!, null, Catalog)).Where(p => p.Severity == Severity.Error);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("always")]
    public void OnlyRoundTrips(string only)
    {
        var load = ConfigYaml.Load(Yaml($"{{ id: a, step: bluetooth.connect, with: {{ device: x }}, only: {only} }}"));
        Assert.Empty(load.Problems);
        Assert.Equal(only, ((StepNode)load.Config!.Flows[0].Nodes[0]).Only);

        var saved = ConfigYaml.Save(load.Config);

        Assert.Contains($"only: {only}", saved, StringComparison.Ordinal);
        Assert.Equal(only, ((StepNode)ConfigYaml.Load(saved).Config!.Flows[0].Nodes[0]).Only);
    }

    [Fact]
    public void NoOnlyWritesNoKey()
    {
        var load = ConfigYaml.Load(Yaml("{ id: a, step: bluetooth.connect, with: { device: x } }"));

        Assert.Null(((StepNode)load.Config!.Flows[0].Nodes[0]).Only);
        Assert.DoesNotContain("only", ConfigYaml.Save(load.Config), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownConditionNamesTheStepAndTheChoices()
    {
        var error = Assert.Single(Errors(Yaml("{ id: a, step: bluetooth.connect, with: { device: x }, only: running }")), e => e.Path.EndsWith(".only", StringComparison.Ordinal));

        Assert.Equal("flows.f.nodes.a.only", error.Path);
        Assert.Contains("disconnected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepTypeWithoutConditionsRefusesOnlyButTakesAlways()
    {
        Assert.Single(Errors(Yaml("{ id: a, step: notify, with: { text: x }, only: running }")));
        Assert.Empty(Errors(Yaml("{ id: a, step: notify, with: { text: x }, only: always }")));
    }

    [Fact]
    public void OnlyOnACardIsAnError()
    {
        var load = ConfigYaml.Load("""
            flows:
              f:
                nodes:
                  - { id: a, step: notify, with: { text: x } }
                  - { id: g, group: [a], only: always }
            """);

        Assert.Contains(load.Problems, p => p.Severity == Severity.Error && p.Path == "flows.f.nodes.g");
    }
}
