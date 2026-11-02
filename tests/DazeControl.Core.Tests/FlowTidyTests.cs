using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class FlowTidyTests
{
    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static string Order(Flow flow) => string.Join(",", flow.Nodes.Select(n => n.Id));

    [Fact]
    public void CrossedPairIsUncrossed()
    {
        // a→y and b→x cross while x sits above y.
        var flow = FlowOf(Step("a"), Step("b"), Step("x", "b"), Step("y", "a"));
        Assert.Equal(1, FlowTidy.Crossings(flow));

        var tidy = FlowTidy.Tidy(flow);

        Assert.Equal(0, FlowTidy.Crossings(tidy));
        Assert.Equal(FlowLayout.Layers(flow), FlowLayout.Layers(tidy));
    }

    [Fact]
    public void AnUncrossedFlowIsReturnedAsIs()
    {
        var flow = FlowOf(Step("a"), Step("b"), Step("x", "a"), Step("y", "b"));

        Assert.Same(flow, FlowTidy.Tidy(flow));
    }

    [Fact]
    public void NodesSwapOnlyAmongTheirColumnsSlots()
    {
        // The card and its members keep their places; only x and y trade slots.
        var flow = FlowOf(
            Step("a"), Step("b"),
            new GroupNode("together", ["m1", "m2"]) { After = [new Edge("a")] },
            Step("m1"), Step("m2"),
            Step("x", "b"), Step("y", "a"));

        var tidy = FlowTidy.Tidy(flow);

        Assert.Equal(0, FlowTidy.Crossings(tidy));
        Assert.Equal("a,b,together,m1,m2,y,x", Order(tidy));
    }

    [Fact]
    public void LongEdgesCountAcrossColumns()
    {
        // a→z skips column 1 and crosses b→c there; a→d crosses it too.
        var flow = FlowOf(Step("a"), Step("b"), Step("c", "b"), Step("d", "a"), Step("w", "c"), Step("z", "a", "d"));
        Assert.Equal(2, FlowTidy.Crossings(flow));

        var tidy = FlowTidy.Tidy(flow);

        Assert.Equal(0, FlowTidy.Crossings(tidy));
        Assert.Equal("a,b,d,c,z,w", Order(tidy));
    }

    [Fact]
    public void ALoopInAHalfBuiltFlowDoesNotThrow()
    {
        var flow = FlowOf(Step("a", "b"), Step("b", "a"), Step("c", "a"));

        var tidy = FlowTidy.Tidy(flow);

        Assert.Equal(3, tidy.Nodes.Count);
    }

    [Fact]
    public void AnEmptyFlowIsFine() => Assert.Equal(0, FlowTidy.Crossings(FlowTidy.Tidy(FlowOf())));
}
