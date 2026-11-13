using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>The call graph of <c>flow.run</c> (D-063, flow-calls/DESIGN.md §4).</summary>
public class FlowCallValidationTests
{
    private static ValueList<Problem> Validate(string flows)
    {
        var load = ConfigYaml.Load("flows:\n" + flows);
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    private static string Flow(string id, string name, params string[] calls) =>
        $"  {id}:\n    name: {name}\n    nodes:\n"
        + (calls.Length == 0 ? "      - { id: s, step: notify, with: { text: x } }\n"
            : string.Concat(calls.Select((c, i) => $"      - {{ id: c{i}, step: flow.run, with: {{ flow: {c} }} }}\n")));

    [Fact]
    public void ACallOfAHealthyFlowIsValid() =>
        Assert.Empty(Validate(Flow("a", "A", "b") + Flow("b", "B")));

    [Fact]
    public void AFlowCantRunItself()
    {
        var problem = Assert.Single(Validate(Flow("a", "A", "a")));
        Assert.Equal((Severity.Error, "flows.a.nodes.c0.with.flow", "A flow can't run itself."),
            (problem.Severity, problem.Path, problem.Message));
    }

    [Fact]
    public void ALoopThroughOthersIsAnErrorOnEveryStepInIt()
    {
        var problems = Validate(Flow("a", "Headphones", "b") + Flow("b", "Make it the default", "a"));

        Assert.All(problems, p => Assert.Equal(Severity.Error, p.Severity));
        Assert.Equal(
            [
                ("a", "That would make a loop: Headphones runs Make it the default, which runs Headphones."),
                ("b", "That would make a loop: Make it the default runs Headphones, which runs Make it the default."),
            ],
            problems.Select(p => (p.FlowId!, p.Message)));
        Assert.Equal(["a", "b"], ConfigValidator.FlowsWithErrors(problems).Order());
    }

    [Fact]
    public void ALongerLoopNamesEveryFlowAndSparesFlowsOutsideIt()
    {
        var problems = Validate(Flow("a", "A", "b") + Flow("b", "B", "c") + Flow("c", "C", "a") + Flow("d", "D", "a"));

        Assert.Equal("That would make a loop: A runs B, which runs C, which runs A.", problems.Single(p => p.FlowId == "a").Message);
        Assert.Equal(["a", "b", "c"], ConfigValidator.FlowsWithErrors(problems).Order());
    }

    [Fact]
    public void CallProblemSaysWhichPicksWouldLoop()
    {
        var flows = ConfigYaml.Load("flows:\n" + Flow("a", "A") + Flow("b", "B", "c") + Flow("c", "C", "a") + Flow("d", "D")).Config!.Flows;

        Assert.Equal("A flow can't run itself.", ConfigValidator.CallProblem(flows, "a", "a"));
        Assert.Equal("That would make a loop: A runs B, which runs C, which runs A.", ConfigValidator.CallProblem(flows, "a", "b"));
        Assert.NotNull(ConfigValidator.CallProblem(flows, "a", "c"));
        Assert.Null(ConfigValidator.CallProblem(flows, "a", "d"));
        Assert.Null(ConfigValidator.CallProblem(flows, "b", "a"));
        Assert.Null(ConfigValidator.CallProblem(flows, "a", "gone"));
    }

    [Fact]
    public void AStartModeCallCountsForLoops()
    {
        var problems = Validate(Flow("a", "A", "b") + """
              b:
                name: B
                nodes:
                  - { id: c0, step: flow.run, with: { flow: a, wait: false } }

            """);

        Assert.Equal(2, problems.Count(p => p.Message.StartsWith("That would make a loop", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnUnknownFlowIsAnError()
    {
        var problem = Assert.Single(Validate(Flow("a", "A", "gone")));
        Assert.Equal((Severity.Error, "gone isn't there anymore. Pick another flow."), (problem.Severity, problem.Message));
    }

    [Fact]
    public void ATurnedOffFlowIsAWarning()
    {
        var problem = Assert.Single(Validate(Flow("a", "A", "b") + Flow("b", "Make it the default") + "    enabled: false\n"));
        Assert.Equal((Severity.Warning, "Make it the default is turned off, so this step will fail. Turn it on in Flows."),
            (problem.Severity, problem.Message));
    }

    [Fact]
    public void AFlowWithErrorsIsAWarningOnTheCaller()
    {
        var problems = Validate(Flow("a", "A", "b") + """
              b:
                name: B
                nodes:
                  - { id: s, step: nope }

            """);

        var warning = Assert.Single(problems, p => p.FlowId == "a");
        Assert.Equal((Severity.Warning, "B needs fixing; this step runs its last working version."), (warning.Severity, warning.Message));
    }

    [Fact]
    public void ACallerOfAFlowInALoopIsWarnedItNeedsFixing()
    {
        var problems = Validate(Flow("c", "C", "a") + Flow("a", "A", "a"));

        var warning = Assert.Single(problems, p => p.FlowId == "c");
        Assert.Equal((Severity.Warning, "A needs fixing; this step runs its last working version."), (warning.Severity, warning.Message));
    }

    [Fact]
    public void APartialCallMakesASucceededRunPartial()
    {
        Assert.Equal(FlowOutcome.PartiallySucceeded, Outcomes.Summarize([(NodeOutcome.Succeeded, false)], partialCall: true));
        Assert.Equal(FlowOutcome.Failed, Outcomes.Summarize([(NodeOutcome.Failed, false)], stopped: true, partialCall: true));
        Assert.True(new NodeResult("c", NodeOutcome.Succeeded)
        {
            Called = new(Guid.Empty, "b", "B", FlowOutcome.PartiallySucceeded, []),
        }.IsPartialCall);
    }
}
