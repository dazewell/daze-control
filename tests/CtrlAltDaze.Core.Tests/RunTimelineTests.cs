using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

public sealed class RunTimelineTests
{
    private static int _order;

    /// <summary>A node that ran from <paramref name="start"/> to <paramref name="end"/> seconds.</summary>
    private static NodeResult Ran(string id, double start, double end, NodeOutcome outcome = NodeOutcome.Succeeded,
        params double[] retries) =>
        new(id, outcome, 1 + retries.Length, Duration: TimeSpan.FromSeconds(end - start), Start: TimeSpan.FromSeconds(start),
            Order: ++_order, Retries: retries.Length == 0 ? null : [.. retries.Select(TimeSpan.FromSeconds)]);

    private static RunRecord Run(params NodeResult[] nodes) =>
        new(Guid.NewGuid(), "f", "F", "Tray", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(nodes.Length == 0 ? 1 : nodes.Max(n => (n.Start + n.Duration).TotalSeconds)),
            FlowOutcome.Succeeded, nodes);

    private static Flow Flow(string nodes)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n" + nodes);
        return load.Config!.Flows[0];
    }

    private static Dictionary<string, int> Lanes(RunTimeline timeline) => timeline.Bars.ToDictionary(b => b.NodeId, b => b.Lane);

    [Fact]
    public void AChainStaysOnOneLane()
    {
        var timeline = RunTimeline.Build(Run(Ran("a", 0, 1), Ran("b", 1, 2), Ran("c", 2, 4)), null);

        Assert.Equal(1, timeline.Lanes);
        Assert.All(timeline.Bars, b => Assert.Equal(0, b.Lane));
        Assert.Equal(TimeSpan.FromSeconds(4), timeline.Length);
    }

    [Fact]
    public void ParallelStepsGetSeparateLanes()
    {
        var timeline = RunTimeline.Build(Run(Ran("a", 0, 3), Ran("b", 0, 1), Ran("c", 0.5, 2)), null);

        Assert.Equal(3, timeline.Lanes);
        Assert.Equal(new Dictionary<string, int> { ["a"] = 0, ["b"] = 1, ["c"] = 2 }, Lanes(timeline));
    }

    [Fact]
    public void AFreedLaneIsReusedLowestFirst()
    {
        // b ends at 1, so d (starting at 2) goes back to lane 1, below c's lane 2.
        var timeline = RunTimeline.Build(Run(Ran("a", 0, 5), Ran("b", 0, 1), Ran("c", 0.5, 4), Ran("d", 2, 3)), null);

        Assert.Equal(3, timeline.Lanes);
        Assert.Equal(1, Lanes(timeline)["d"]);
    }

    [Fact]
    public void ABarStartingWhenAnotherEndsSharesItsLane()
    {
        var timeline = RunTimeline.Build(Run(Ran("a", 0, 2), Ran("b", 2, 3)), null);

        Assert.Equal(1, timeline.Lanes);
    }

    [Fact]
    public void ACardTakesNeighbouringLanesAndGetsABracket()
    {
        // At 1 s lane 0 is free again but lane 1 (x) isn't: the card takes lanes 2–3, never 0 and 2.
        var flow = Flow("""
                  - { id: x, step: notify, with: { text: x } }
                  - { id: y, step: notify, with: { text: x } }
                  - { id: m1, step: notify, with: { text: x } }
                  - { id: m2, step: notify, with: { text: x } }
                  - { id: card, group: [m1, m2] }
            """);
        var timeline = RunTimeline.Build(Run(Ran("y", 0, 0.5), Ran("x", 0, 5), Ran("m1", 1, 2), Ran("m2", 1, 3)), flow);

        var lanes = Lanes(timeline);
        Assert.Equal(0, lanes["y"]);
        Assert.Equal(1, lanes["x"]);
        Assert.Equal(2, lanes["m1"]);
        Assert.Equal(3, lanes["m2"]);
        var bracket = Assert.Single(timeline.Brackets);
        Assert.Equal(("card", 2, 3), (bracket.CardId, bracket.FirstLane, bracket.LastLane));
        Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)), (bracket.Start, bracket.End));
    }

    [Fact]
    public void NothingLandsInsideACardsBracketUntilItsLastMemberEnds()
    {
        var flow = Flow("""
                  - { id: m1, step: notify, with: { text: x } }
                  - { id: m2, step: notify, with: { text: x } }
                  - { id: card, group: [m1, m2] }
                  - { id: z, step: notify, with: { text: x } }
            """);
        // m1 ends at 1, but the card runs to 3: z (starting at 2) must not take m1's lane.
        var timeline = RunTimeline.Build(Run(Ran("m1", 0, 1), Ran("m2", 0, 3), Ran("z", 2, 4)), flow);

        Assert.Equal(2, Lanes(timeline)["z"]);
    }

    [Fact]
    public void StepsThatNeverStartedHaveNoBar()
    {
        var run = Run(Ran("a", 0, 1, NodeOutcome.Failed), new NodeResult("b", NodeOutcome.Skipped, Attempts: 0));

        var timeline = RunTimeline.Build(run, null);

        Assert.Equal(["a"], timeline.Bars.Select(b => b.NodeId));
        Assert.Equal(NodeOutcome.Failed, timeline.Bars[0].Outcome);
    }

    [Fact]
    public void RetriesBecomeNotches()
    {
        var timeline = RunTimeline.Build(Run(Ran("a", 0, 6, NodeOutcome.Succeeded, 2, 4)), null);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], timeline.Bars[0].Notches);
    }

    [Fact]
    public void WithoutTheFlowCardMembersPackAsSteps()
    {
        var timeline = RunTimeline.Build(Run(Ran("m1", 0, 1), Ran("m2", 0, 1)), null);

        Assert.Empty(timeline.Brackets);
        Assert.Equal(2, timeline.Lanes);
    }

    [Fact]
    public void AnEmptyRunHasNoLanes()
    {
        var timeline = RunTimeline.Build(Run(), null);

        Assert.Equal(0, timeline.Lanes);
        Assert.Equal(TimeSpan.FromSeconds(1), timeline.Length);
    }
}
