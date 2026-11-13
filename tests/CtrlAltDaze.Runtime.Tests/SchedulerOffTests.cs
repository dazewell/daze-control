using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Runtime.Scheduling;
using static CtrlAltDaze.Runtime.Tests.SchedulerTests;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>A step turned off (<c>enabled: false</c>, T25.4, D-148): done at once, never handed to the runner.</summary>
public class SchedulerOffTests
{
    private const string Off = "enabled: false";

    [Fact]
    public void FollowersRunAndTheOffStepIsNeverStarted()
    {
        var scheduler = new Scheduler(Flow(Step("a"), Step("b", "after: [a], " + Off), Step("c", "after: [b]")));

        Assert.Equal([["a"], ["c"]], Run(scheduler));
        AssertOutcomes(scheduler, ("a", NodeOutcome.Succeeded), ("b", NodeOutcome.Succeeded), ("c", NodeOutcome.Succeeded));
        Assert.True(scheduler.IsOff("b"));
        Assert.Equal(FlowOutcome.Succeeded, scheduler.Result);
    }

    [Fact]
    public void AnOffFirstStepLetsItsFollowersStartAtOnce()
    {
        var scheduler = new Scheduler(Flow(Step("a", Off), Step("b", "after: [a]"), Step("c", "after: [{ id: a, when: always }]")));

        Assert.Equal(["b", "c"], scheduler.Start());
    }

    [Fact]
    public void EveryStepOffFinishesWithoutRunningAnything()
    {
        var scheduler = new Scheduler(Flow(Step("a", Off), Step("b", "after: [a], " + Off)));

        Assert.Empty(scheduler.Start());
        Assert.True(scheduler.IsFinished);
        Assert.Equal(FlowOutcome.Succeeded, scheduler.Result);
    }

    [Fact]
    public void ACardWithOneMemberOffWaitsForTheOthers()
    {
        var scheduler = new Scheduler(Flow(Step("a"), Step("b", Off), "{ id: card, group: [a, b] }", Step("c", "after: [card]")));

        Assert.Equal(["a"], scheduler.Start());
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["b"]);
        Assert.False(scheduler.Outcomes.ContainsKey("card"));
        Assert.Equal(["c"], scheduler.Complete("a", StepResult.Succeeded));
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["card"]);
    }

    [Fact]
    public void ACardWithEveryMemberOffIsDoneAtOnce()
    {
        var scheduler = new Scheduler(Flow(Step("a", Off), Step("b", Off), "{ id: card, group: [a, b] }", Step("c", "after: [card]")));

        Assert.Equal(["c"], scheduler.Start());
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["card"]);
    }

    [Fact]
    public void AFailureLinkFromAnOffStepNeverFires()
    {
        var scheduler = new Scheduler(Flow(
            Step("a", Off),
            Step("fix", "after: [{ id: a, when: failure }]"),
            Step("next", "after: [a]")));

        Assert.Equal([["next"]], Run(scheduler));
        AssertOutcomes(scheduler, ("a", NodeOutcome.Succeeded), ("fix", NodeOutcome.NotNeeded), ("next", NodeOutcome.Succeeded));
        Assert.Equal(FlowOutcome.Succeeded, scheduler.Result);
    }

    [Fact]
    public void AnOffFallbackThatIsNeededCountsAsDoneAndWhatFollowsRuns()
    {
        var scheduler = new Scheduler(Flow(
            Step("a"),
            Step("fix", "after: [{ id: a, when: failure }], " + Off),
            Step("next", "after: [a, fix]")));

        Assert.Equal([["a"], ["next"]], Run(scheduler, ("a", StepResult.Failed)));
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["fix"]);
        Assert.Equal(NodeOutcome.Succeeded, scheduler.Outcomes["next"]);
    }

    [Fact]
    public void AnOffStepAfterAFailureIsSkippedLikeAnyOther()
    {
        var scheduler = new Scheduler(Flow(Step("a", "onFailure: skip"), Step("b", "after: [a], " + Off), Step("c")));

        Run(scheduler, ("a", StepResult.Failed));
        Assert.Equal(NodeOutcome.Skipped, scheduler.Outcomes["b"]);
    }
}
