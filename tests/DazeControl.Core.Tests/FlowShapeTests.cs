using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Core.Tests;

public class FlowShapeTests
{
    private static StepNode Step(string id, params Edge[] after) => new(id, "notify") { After = [.. after] };

    /// <summary>The user's Connect Technics flow: Technics, else Sony and the speakers.</summary>
    private static readonly Flow Technics = new("connect-technics", "Connect Technics",
    [
        Step("bt") with { Name = "Connect Technics" },
        Step("out", new Edge("bt")),
        Step("connect", new Edge("bt", EdgeWhen.Failure)) with { Name = "Connect Sony" },
        Step("setDefault", new Edge("connect")),
    ]);

    [Fact]
    public void FallbacksAreFailureTargetsAndWhatWaitsForTheirSuccess()
    {
        Assert.Equal(["connect", "setDefault"], FlowShape.Fallbacks(Technics).Order());
        Assert.Equal(["bt"], FlowShape.WithFallback(Technics));
    }

    [Fact]
    public void AnAlwaysEdgeEndsTheFallbackAndCardsBringTheirMembers()
    {
        var flow = new Flow("f", "F",
        [
            Step("hp"), Step("a"), Step("b"),
            new GroupNode("g", ["a", "b"]) { After = [new Edge("hp", EdgeWhen.Failure)] },
            Step("next", new Edge("g")),
            Step("end", new Edge("g", EdgeWhen.Always)),
        ]);

        Assert.Equal(["a", "b", "g", "next"], FlowShape.Fallbacks(flow).Order());
        Assert.Equal("g", FlowShape.UnitOf(flow, "a"));
        Assert.Equal("hp", FlowShape.UnitOf(flow, "hp"));
    }

    [Theory]
    [InlineData(new[] { NodeOutcome.Succeeded, NodeOutcome.NotNeeded }, new[] { false, false }, FlowOutcome.Succeeded)]
    [InlineData(new[] { NodeOutcome.Failed, NodeOutcome.Succeeded }, new[] { true, false }, FlowOutcome.Succeeded)]
    [InlineData(new[] { NodeOutcome.FailedIgnored, NodeOutcome.Succeeded }, new[] { true, false }, FlowOutcome.Succeeded)]
    [InlineData(new[] { NodeOutcome.Skipped, NodeOutcome.Succeeded }, new[] { true, false }, FlowOutcome.Succeeded)]
    [InlineData(new[] { NodeOutcome.Skipped, NodeOutcome.Succeeded }, new[] { false, false }, FlowOutcome.PartiallySucceeded)]
    [InlineData(new[] { NodeOutcome.Failed, NodeOutcome.FailedIgnored }, new[] { true, false }, FlowOutcome.Failed)]
    [InlineData(new[] { NodeOutcome.Cancelled, NodeOutcome.Succeeded }, new[] { true, false }, FlowOutcome.PartiallySucceeded)]
    public void SummaryCountsHandledFailuresAndNotNeededAsFine(NodeOutcome[] outcomes, bool[] handled, FlowOutcome expected) =>
        Assert.Equal(expected, Outcomes.Summarize(outcomes.Zip(handled)));

    [Fact]
    public void FixNamesTheHandledFailureAndTheFallbackThatRan()
    {
        NodeResult[] results =
        [
            new("bt", NodeOutcome.Failed, Order: 1), new("out", NodeOutcome.Skipped, 0),
            new("connect", NodeOutcome.Succeeded, Order: 2), new("setDefault", NodeOutcome.Succeeded, Order: 3),
        ];

        Assert.Equal(("bt", "connect"), Outcomes.Fix(Technics, results));
    }

    [Fact]
    public void NoFixWhenNothingFailedOrTheFallbackNeverStarted()
    {
        Assert.Null(Outcomes.Fix(Technics,
        [
            new("bt", NodeOutcome.Succeeded, Order: 1), new("out", NodeOutcome.Succeeded, Order: 2),
            new("connect", NodeOutcome.NotNeeded, 0), new("setDefault", NodeOutcome.NotNeeded, 0),
        ]));
        Assert.Null(Outcomes.Fix(Technics, [new("bt", NodeOutcome.Failed, Order: 1), new("connect", NodeOutcome.Cancelled, 0)]));
    }

    [Fact]
    public void AFallbackCardIsNamedByItsFirstMemberThatStarted()
    {
        var flow = new Flow("f", "F",
        [
            Step("hp"), Step("a"), Step("b"),
            new GroupNode("g", ["a", "b"]) { After = [new Edge("hp", EdgeWhen.Failure)] },
        ]);

        Assert.Equal(("hp", "b"), Outcomes.Fix(flow,
            [new("hp", NodeOutcome.Failed, Order: 1), new("a", NodeOutcome.Succeeded, Order: 3), new("b", NodeOutcome.Succeeded, Order: 2)]));
    }
}
