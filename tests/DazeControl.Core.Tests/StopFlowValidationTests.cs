using DazeControl.Core.Catalog;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T24.1 (D-133): the Stop flow step in the catalog and the validator.</summary>
public class StopFlowValidationTests
{
    private static Core.Model.ValueList<Problem> Validate(params string[] nodes)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n")));
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Fact]
    public void AStopAfterACheckFailureIsValid() =>
        Assert.Empty(Validate("{ id: c, step: flag.check, with: { name: home }, onFailure: skip }",
            "{ id: s, step: flow.stop, with: { mode: failed, message: \"Not at home\" }, after: [{ id: c, when: failure }] }"));

    [Fact]
    public void AStopTakesValuesInItsMessage() =>
        Assert.Empty(Validate("{ id: m, step: text.make, with: { text: x } }",
            "{ id: s, step: flow.stop, with: { message: \"Stopped at {@time}\" }, after: [m] }"));

    [Fact]
    public void AnUnknownModeIsAnError()
    {
        var problem = Assert.Single(Validate("{ id: s, step: flow.stop, with: { mode: loudly } }"));

        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal("flows.f.nodes.s.with.mode", problem.Path);
    }

    [Fact]
    public void AStepAfterAStopIsAWarning()
    {
        var problem = Assert.Single(Validate("{ id: s, step: flow.stop }", "{ id: w, step: wait, with: { duration: 1s }, after: [s] }"));

        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Equal("flows.f.nodes.w.after", problem.Path);
    }

    [Fact]
    public void AFailureLinkAfterAQuietStopIsAWarningButNotAfterAStopAsFailed()
    {
        var quiet = Assert.Single(Validate("{ id: s, step: flow.stop }", "{ id: w, step: wait, with: { duration: 1s }, after: [{ id: s, when: failure }] }"));
        Assert.Equal("flows.f.nodes.w.after", quiet.Path);

        Assert.Empty(Validate("{ id: s, step: flow.stop, with: { mode: failed } }", "{ id: w, step: wait, with: { duration: 1s }, after: [{ id: s, when: failure }] }"));
    }

    [Fact]
    public void AStepAfterACardHoldingAStopIsAWarning()
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: s, step: flow.stop }\n      - { id: a, step: wait, with: { duration: 1s } }\n      - { id: card, group: [s, a] }\n      - { id: w, step: wait, with: { duration: 1s }, after: [card] }\n");
        Assert.Empty(load.Problems);

        var problem = Assert.Single(ConfigValidator.Validate(load.Config!));

        Assert.Equal("flows.f.nodes.w.after", problem.Path);
    }

    [Fact]
    public void TheStepIsInTheFlowGroupWithPlainWording()
    {
        var stop = ProviderCatalog.Find(ProviderCatalog.StopFlowId)!;

        Assert.Equal("Stop flow", stop.Title);
        Assert.Equal(["Quietly", "As failed"], stop.FindParam("mode")!.Captions);
        Assert.True(stop.FindParam("message")!.TakesValues);
        Assert.Empty(stop.Resources);
        Assert.All(stop.Params.Select(p => p.Title + " " + p.Help).Append(stop.Title), t => Assert.DoesNotMatch(@"(?i)\bnode|\bDAG\b|\bOSD\b", t));
    }
}
