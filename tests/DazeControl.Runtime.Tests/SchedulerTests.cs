using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Scheduling;

namespace DazeControl.Runtime.Tests;

public class SchedulerTests
{
    /// <summary>A flow "f" from YAML node lines; every step is a notify, so only structure matters.</summary>
    internal static Flow Flow(params string[] nodes)
    {
        var yaml = "flows:\n  f:\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n"));
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        Assert.DoesNotContain(ConfigValidator.Validate(load.Config!), p => p.Severity == Severity.Error);
        return load.Config!.Flows[0];
    }

    internal static string Step(string id, string extra = "") =>
        $"{{ id: {id}, step: notify, with: {{ text: x }}{(extra.Length > 0 ? ", " + extra : "")} }}";

    /// <summary>
    /// Runs the scheduler to the end: steps complete in start order with the given results (default success),
    /// each "wave" being what was ready at once. Returns the waves.
    /// </summary>
    private static List<string[]> Run(Scheduler scheduler, params (string Step, StepResult Result)[] results)
    {
        var lookup = results.ToDictionary(r => r.Step, r => r.Result);
        var waves = new List<string[]>();
        var queue = new Queue<string>(scheduler.Start());
        if (queue.Count > 0)
            waves.Add([.. queue]);
        while (queue.Count > 0)
        {
            var step = queue.Dequeue();
            if (!scheduler.Running.Contains(step))
                continue;
            var result = scheduler.IsStopped || scheduler.IsCancelled ? StepResult.Cancelled
                : lookup.GetValueOrDefault(step, StepResult.Succeeded);
            var next = scheduler.Complete(step, result);
            if (next.Count > 0)
                waves.Add([.. next]);
            foreach (var id in next)
                queue.Enqueue(id);
        }
        Assert.True(scheduler.IsFinished);
        Assert.Empty(scheduler.Running);
        return waves;
    }

    private static void AssertOutcomes(Scheduler scheduler, params (string Id, NodeOutcome Outcome)[] expected) =>
        Assert.Equal(expected.OrderBy(e => e.Id), scheduler.Outcomes.Select(o => (o.Key, o.Value)).OrderBy(e => e.Key));

    [Fact]
    public void ChainRunsInOrder()
    {
        var scheduler = new Scheduler(Flow(Step("a"), Step("b", "after: [a]"), Step("c", "after: [b]")));

        Assert.Equal([["a"], ["b"], ["c"]], Run(scheduler));
        Assert.Equal(FlowOutcome.Succeeded, scheduler.Result);
    }

    [Fact]
    public void RootsStartTogetherAndJoinWaitsForAllInputs()
    {
        var scheduler = new Scheduler(Flow(Step("a"), Step("b"), Step("j", "after: [a, b]")));

        Assert.Equal(["a", "b"], scheduler.Start());
        Assert.Empty(scheduler.Complete("a", StepResult.Succeeded));
        Assert.Equal(["j"], scheduler.Complete("b", StepResult.Succeeded));
    }

    [Fact]
    public void FailureEdgeHandlesTheFailure()
    {
        // D-024: "buds if headphones fail"; hp keeps the default stop policy.
        var scheduler = new Scheduler(Flow(
            Step("hp"),
            Step("buds", "after: [{ id: hp, when: failure }]"),
            Step("eq", "after: [hp]"),
            Step("done", "after: [{ id: hp, when: always }]")));

        Run(scheduler, ("hp", StepResult.Failed));

        Assert.False(scheduler.IsStopped);
        AssertOutcomes(scheduler, ("hp", NodeOutcome.Failed), ("buds", NodeOutcome.Succeeded),
            ("eq", NodeOutcome.Skipped), ("done", NodeOutcome.Succeeded));
        Assert.Equal(FlowOutcome.PartiallySucceeded, scheduler.Result);
    }

    [Fact]
    public void FailureEdgeFromASuccessIsSkipped()
    {
        var scheduler = new Scheduler(Flow(Step("hp"), Step("buds", "after: [{ id: hp, when: failure }]"), Step("x", "after: [buds]")));

        Run(scheduler);

        AssertOutcomes(scheduler, ("hp", NodeOutcome.Succeeded), ("buds", NodeOutcome.Skipped), ("x", NodeOutcome.Skipped));
        Assert.Equal(FlowOutcome.PartiallySucceeded, scheduler.Result);
    }

    [Fact]
    public void StopCancelsPendingAndAsksToCancelRunning()
    {
        var scheduler = new Scheduler(Flow(Step("a"), Step("slow"), Step("b", "after: [a]"), Step("c", "after: [{ id: a, when: always }]")));

        Assert.Equal(["a", "slow"], scheduler.Start());
        Assert.Empty(scheduler.Complete("a", StepResult.Failed));
        Assert.True(scheduler.IsStopped);
        Assert.False(scheduler.IsFinished);
        Assert.Equal(["slow"], scheduler.Running);
        Assert.Empty(scheduler.Complete("slow", StepResult.Cancelled));

        AssertOutcomes(scheduler, ("a", NodeOutcome.Failed), ("slow", NodeOutcome.Cancelled),
            ("b", NodeOutcome.Cancelled), ("c", NodeOutcome.Cancelled));
        Assert.Equal(FlowOutcome.Failed, scheduler.Result);
    }

    [Fact]
    public void SkipPolicySkipsDependentsButAlwaysEdgesAndOtherBranchesRun()
    {
        var scheduler = new Scheduler(Flow(
            Step("a", "onFailure: skip"),
            Step("b", "after: [a]"),
            Step("c", "after: [b]"),
            Step("d", "after: [{ id: b, when: always }]"),
            Step("e")));

        Run(scheduler, ("a", StepResult.Failed));

        AssertOutcomes(scheduler, ("a", NodeOutcome.Failed), ("b", NodeOutcome.Skipped), ("c", NodeOutcome.Skipped),
            ("d", NodeOutcome.Succeeded), ("e", NodeOutcome.Succeeded));
        Assert.Equal(FlowOutcome.PartiallySucceeded, scheduler.Result);
    }

    [Fact]
    public void IgnorePolicyCountsAsDoneAndAlsoFiresFailureEdges()
    {
        var scheduler = new Scheduler(Flow(
            Step("a", "onFailure: ignore"), Step("b", "after: [a]"), Step("fix", "after: [{ id: a, when: failure }]")));

        Run(scheduler, ("a", StepResult.Failed));

        AssertOutcomes(scheduler, ("a", NodeOutcome.FailedIgnored), ("b", NodeOutcome.Succeeded), ("fix", NodeOutcome.Succeeded));
        Assert.Equal(FlowOutcome.PartiallySucceeded, scheduler.Result);
    }

    [Fact]
    public void CardStartsMembersTogetherAndFinishesWhenAllHave()
    {
        var scheduler = new Scheduler(Flow(
            Step("z"), Step("a"), Step("b"), "{ id: g, group: [a, b], after: [z] }", Step("next", "after: [g]")));

        Assert.Equal([["z"], ["a", "b"], ["next"]], Run(scheduler));
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["g"]);
    }

    [Fact]
    public void CardWithAFailedMemberFailsAndItsOwnPolicyApplies()
    {
        var scheduler = new Scheduler(Flow(
            Step("a"), Step("b"), "{ id: g, group: [a, b], onFailure: skip }", Step("next", "after: [g]"), Step("other")));

        Run(scheduler, ("a", StepResult.Failed));

        Assert.False(scheduler.IsStopped);
        AssertOutcomes(scheduler, ("a", NodeOutcome.Failed), ("b", NodeOutcome.Succeeded), ("g", NodeOutcome.Failed),
            ("next", NodeOutcome.Skipped), ("other", NodeOutcome.Succeeded));
    }

    [Fact]
    public void CardWithDefaultPolicyStopsTheFlowWhenAMemberFails()
    {
        var scheduler = new Scheduler(Flow(Step("a"), Step("b"), "{ id: g, group: [a, b] }", Step("next", "after: [g]")));

        Run(scheduler, ("b", StepResult.Failed));

        Assert.True(scheduler.IsStopped);
        Assert.Equal(NodeOutcome.Cancelled, scheduler.Outcomes["next"]);
        Assert.Equal(FlowOutcome.Failed, scheduler.Result);
    }

    [Fact]
    public void MayFailMemberDoesNotFailTheCard()
    {
        var scheduler = new Scheduler(Flow(Step("a", "onFailure: ignore"), Step("b"), "{ id: g, group: [a, b] }", Step("next", "after: [g]")));

        Run(scheduler, ("a", StepResult.Failed));

        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["g"]);
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["next"]);
    }

    [Fact]
    public void SkippedCardSkipsItsMembers()
    {
        var scheduler = new Scheduler(Flow(
            Step("z", "onFailure: skip"), Step("a"), Step("b"), "{ id: g, group: [a, b], after: [z] }"));

        Run(scheduler, ("z", StepResult.Failed));

        AssertOutcomes(scheduler, ("z", NodeOutcome.Failed), ("a", NodeOutcome.Skipped), ("b", NodeOutcome.Skipped),
            ("g", NodeOutcome.Skipped));
    }

    [Fact]
    public void FailureEdgeFromACardHandlesItsFailure()
    {
        var scheduler = new Scheduler(Flow(
            Step("a"), Step("b"), "{ id: g, group: [a, b] }", Step("fallback", "after: [{ id: g, when: failure }]")));

        Run(scheduler, ("a", StepResult.Failed));

        Assert.False(scheduler.IsStopped);
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["fallback"]);
    }

    [Fact]
    public void ExternalCancelEndsPendingAndRunningAsCancelled()
    {
        var scheduler = new Scheduler(Flow(Step("a"), Step("b", "after: [a]")));

        scheduler.Start();
        scheduler.Cancel();
        Assert.Empty(scheduler.Complete("a", StepResult.Cancelled));

        AssertOutcomes(scheduler, ("a", NodeOutcome.Cancelled), ("b", NodeOutcome.Cancelled));
        Assert.Equal(FlowOutcome.Cancelled, scheduler.Result);
    }

    [Fact]
    public void CompletingAStepThatIsNotRunningThrows() =>
        Assert.Throws<InvalidOperationException>(() => new Scheduler(Flow(Step("a"))).Complete("a", StepResult.Succeeded));

    [Fact]
    public void EmptyFlowFinishesAtOnce()
    {
        var scheduler = new Scheduler(new Flow("f", "f", []));

        Assert.Empty(scheduler.Start());
        Assert.True(scheduler.IsFinished);
        Assert.Equal(FlowOutcome.Succeeded, scheduler.Result);
    }
}
