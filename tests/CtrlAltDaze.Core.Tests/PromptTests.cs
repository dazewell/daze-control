using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>Prompts (M20, D-107): <c>ask: true</c> inputs and the <c>prompt.ask</c> step in YAML and validation.</summary>
public class PromptTests
{
    private const string Library = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }

        """;

    private static string AskFlow(string input = "{ kind: audio, name: Output, default: technics, ask: true, timeout: 20s, onTimeout: stop }",
        string step = "{ id: s, step: audio.setDefault, with: { device: $output } }") =>
        Library + $"""
        flows:
          f:
            inputs:
              output: {input}
            nodes:
              - {step}

        """;

    private static string StepFlow(string with, string extra = "") =>
        Library + $$"""
        flows:
          f:
            nodes:
              - { id: q, step: prompt.ask, with: { {{with}} }{{extra}} }

        """;

    private static List<Problem> Problems(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        return [.. load.Problems, .. ConfigValidator.Validate(load.Config!)];
    }

    private static string[] Errors(string yaml) => [.. Problems(yaml).Where(p => p.Severity == Severity.Error).Select(p => p.Path)];

    [Fact]
    public void AnAskedInputWithoutOnTimeoutSavesAndRoundTrips()
    {
        var load = ConfigYaml.Load(AskFlow("{ kind: audio, default: technics, ask: true }"));
        Assert.Empty(load.Problems);

        var saved = ConfigYaml.Save(load.Config!);

        Assert.DoesNotContain("onTimeout", saved, StringComparison.Ordinal);
        Assert.Equal(load.Config!.Flows[0].Inputs["output"], ConfigYaml.Load(saved).Config!.Flows[0].Inputs["output"]);
    }

    [Fact]
    public void AnAskedInputReadsAndSavesItsPromptSettings()
    {
        var load = ConfigYaml.Load(AskFlow());
        Assert.Empty(load.Problems);
        var input = load.Config!.Flows[0].Inputs["output"];
        Assert.Equal((true, TimeSpan.FromSeconds(20), "stop"), (input.Ask, input.Timeout, input.OnTimeout));
        Assert.Empty(ConfigValidator.Validate(load.Config!));

        var again = ConfigYaml.Load(ConfigYaml.Save(load.Config!));
        Assert.Equal(input, again.Config!.Flows[0].Inputs["output"]);
    }

    [Fact]
    public void AnInputThatIsNotAskedSavesNoPromptSettings()
    {
        var load = ConfigYaml.Load(AskFlow("{ kind: audio, default: technics }"));
        var saved = ConfigYaml.Save(load.Config!);
        Assert.DoesNotContain("ask", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("timeout", saved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ kind: audio, default: technics, ask: true, timeout: 2s }", "flows.f.inputs.output.timeout")]
    [InlineData("{ kind: audio, default: technics, ask: true, timeout: 11m }", "flows.f.inputs.output.timeout")]
    [InlineData("{ kind: audio, default: technics, ask: true, onTimeout: skip }", "flows.f.inputs.output.onTimeout")]
    [InlineData("{ kind: audio, default: technics, ask: true, timeout: soon }", "flows.f.inputs.output.timeout")]
    [InlineData("{ kind: audio, default: technics, ask: perhaps }", "flows.f.inputs.output.ask")]
    public void ABadAskedInputIsAProblemOfThatFlowOnly(string input, string path)
    {
        var problems = Problems(AskFlow(input));
        Assert.Contains(problems, p => p.Path == path && p.Severity == Severity.Error && p.FlowId is null or "f");
    }

    [Fact]
    public void AGoodConfirmAndChooseHaveNoProblems()
    {
        Assert.Empty(Problems(StepFlow("question: Go on?, flag: ok, onTimeout: use-default, default: no, timeout: 10s", ", timeout: 2m")));
        Assert.Empty(Problems(StepFlow("mode: choose, question: Which?, choices: [Tea, Coffee], default: Tea, onTimeout: use-default")));
    }

    [Theory]
    [InlineData("mode: choose, question: Which?, choices: [Tea]")]
    [InlineData("mode: choose, question: Which?")]
    [InlineData("mode: choose, question: Which?, choices: [A, B, C, D, E, F, G, H, I, J, K, L, M]")]
    [InlineData("mode: choose, question: Which?, choices: [Tea, tea]")]
    [InlineData("mode: choose, question: Which?, choices: [Tea, Coffee], default: Milk")]
    public void ChooseNeedsTwoToTwelveDistinctAnswersAndAKnownDefault(string with)
    {
        var errors = Errors(StepFlow(with));
        Assert.Single(errors);
        Assert.Matches(@"\.with\.(choices|default)$", errors[0]);
    }

    [Theory]
    [InlineData("question: Go on?, onTimeout: use-default", "flows.f.nodes.q.with.default")]
    [InlineData("question: Go on?, onTimeout: use-default, default: maybe", "flows.f.nodes.q.with.default")]
    [InlineData("question: Go on?, timeout: 1s", "flows.f.nodes.q.with.timeout")]
    [InlineData("question: Go on?, timeout: 20m", "flows.f.nodes.q.with.timeout")]
    [InlineData("question: Go on?, onTimeout: later", "flows.f.nodes.q.with.onTimeout")]
    [InlineData("mode: ask, question: Go on?", "flows.f.nodes.q.with.mode")]
    [InlineData("mode: confirm", "flows.f.nodes.q.with")]
    public void ABadPromptStepIsAnErrorOnItsOwnSetting(string with, string path) =>
        Assert.Contains(path, Errors(StepFlow(with)));

    [Fact]
    public void TheStepsTimeLimitMustOutlastTheWait() =>
        Assert.Contains("flows.f.nodes.q.timeout", Errors(StepFlow("question: Go on?, timeout: 1m", ", timeout: 30s")));
}
