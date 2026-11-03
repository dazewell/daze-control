using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

/// <summary>Long simple chains wrap onto rows; forks, joins and parallel branches never do (D-060).</summary>
public class FlowWrapTests
{
    private static readonly Size Pill = new(232, 56);

    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static FlowArrangement Arrange(Flow flow) => FlowLayout.Arrange(flow, _ => Pill);

    /// <summary>s1 → s2 → … → s<paramref name="count"/>, plus whatever else.</summary>
    private static List<Node> Chain(int count, string prefix = "s", string? after = null) =>
        [.. Enumerable.Range(1, count).Select(i => (Node)(i == 1 ? (after is null ? Step($"{prefix}1") : Step($"{prefix}1", after)) : Step($"{prefix}{i}", $"{prefix}{i - 1}")))];

    private static Rect B(FlowArrangement a, string id) => a.Find(id)!.Bounds;

    [Fact]
    public void ATenStepChainWrapsIntoRowsOfFiveAndWhatFollowsStartsAfterTheBlock()
    {
        // x forks, so it ends the run and follows the block.
        var flow = FlowOf([.. Chain(10), Step("x", "s10"), Step("y", "x"), Step("z", "x")]);

        var a = Arrange(flow);

        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(B(a, "s1").Y, B(a, $"s{i}").Y);
            Assert.Equal(B(a, $"s{i}").X, B(a, $"s{i + 5}").X);
            Assert.Equal(B(a, "s1").Bottom + 48, B(a, $"s{i + 5}").Y);
        }
        Assert.Equal(B(a, "s5").Right + 64, B(a, "x").X);
        Assert.Equal(B(a, "s10").Y, B(a, "x").Y);
        Assert.Equal(B(a, "x").CenterY, (B(a, "y").Y + B(a, "z").Bottom) / 2, 3);
        Assert.Equal(B(a, "x").Right + 64, B(a, "y").X);
        // Columns stay layers, so keyboard order and Tidy up are untouched.
        Assert.Equal(12, a.Columns.Count); // s1…s10, x, y+z
        Assert.Equal(5, a.Find("s6")!.Column);
        Assert.Equal(B(a, "y").Right + 48, a.Extent.Width);
        Assert.Equal(B(a, "z").Bottom + 48, a.Extent.Height);
    }

    [Fact]
    public void OnlyTheRowBreakEdgeIsWrapped()
    {
        var a = Arrange(FlowOf([.. Chain(10)]));

        var wrapped = Assert.Single(a.Edges, e => e.WrapLaneY is not null);
        Assert.Equal(("s5", "s6"), (wrapped.From, wrapped.To));
        Assert.Equal(B(a, "s1").Bottom + 24, wrapped.WrapLaneY!.Value, 3);
    }

    [Fact]
    public void AChainOfFiveDoesNotWrap()
    {
        var a = Arrange(FlowOf([.. Chain(5)]));

        Assert.All(a.Boxes, b => Assert.Equal(B(a, "s1").Y, b.Bounds.Y));
        Assert.DoesNotContain(a.Edges, e => e.WrapLaneY is not null);
    }

    [Fact]
    public void AChainBesideAParallelBranchDoesNotWrap()
    {
        var a = Arrange(FlowOf([.. Chain(8), .. Chain(8, "p")]));

        Assert.All(Enumerable.Range(1, 8), i => Assert.Equal(B(a, "s1").Y, B(a, $"s{i}").Y));
        Assert.DoesNotContain(a.Edges, e => e.WrapLaneY is not null);
    }

    [Fact]
    public void AForkWhoseLongArmRunsBesideADirectEdgeToItsJoinDoesNotWrap()
    {
        var a = Arrange(FlowOf([Step("f"), .. Chain(8, "s", "f"), Step("j", "s8", "f")]));

        Assert.All(Enumerable.Range(1, 8), i => Assert.Equal(B(a, "s1").Y, B(a, $"s{i}").Y));
        Assert.DoesNotContain(a.Edges, e => e.WrapLaneY is not null);
    }

    [Fact]
    public void AJoinNeverStartsARun()
    {
        // a, b → j → s1 … s5: the run is s1…s5 (five boxes), so nothing wraps; with j it would be six.
        var a = Arrange(FlowOf([Step("a"), Step("b"), Step("j", "a", "b"), .. Chain(5, "s", "j")]));

        Assert.DoesNotContain(a.Edges, e => e.WrapLaneY is not null);
        Assert.Equal(B(a, "j").CenterY, B(a, "s5").CenterY, 3);
    }

    [Fact]
    public void AForkJoinFlowIsUnchanged()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b", "c"), Step("e", "d"));

        var wrapped = Arrange(flow);
        var plain = FlowLayout.Arrange(flow, _ => Pill, new LayoutOptions { WrapAfter = int.MaxValue });

        Assert.Equal(plain.Boxes, wrapped.Boxes);
        Assert.Equal(plain.Edges, wrapped.Edges);
    }

    [Fact]
    public void ACardInsideAChainCountsAsOneBox()
    {
        var nodes = Chain(6);
        nodes.Insert(3, new GroupNode("card", ["m1", "m2"]) { After = [new Edge("s3")] });
        nodes.Insert(4, Step("m1"));
        nodes.Insert(5, Step("m2"));
        nodes[^3] = Step("s4", "card");

        var a = Arrange(FlowOf([.. nodes]));

        // s1 s2 s3 card s4 | s5 s6: seven boxes, rows of five and two; members follow their card.
        Assert.Equal(B(a, "card").CenterY, B(a, "s4").CenterY, 3);
        Assert.Equal(B(a, "s1").X, B(a, "s5").X);
        Assert.Equal(B(a, "card").Bottom + 48, B(a, "s5").Y);
        var wrapped = Assert.Single(a.Edges, e => e.WrapLaneY is not null);
        Assert.Equal(("s4", "s5"), (wrapped.From, wrapped.To));
        Assert.True(B(a, "card").Contains(new Point(B(a, "m1").X + 1, B(a, "m1").Y + 1)));
    }

    [Fact]
    public void TidyUpAndRunAfterOnAWrappedFlowKeepNodesAsBefore()
    {
        var flow = FlowOf([.. Chain(10)]);
        Assert.Contains(Arrange(flow).Edges, e => e.WrapLaneY is not null);

        Assert.Equal(flow.Nodes.Select(n => n.Id), FlowTidy.Tidy(flow).Nodes.Select(n => n.Id));
        var (moved, _) = FlowEdits.RunAfter(flow, "s10", "s2");
        Assert.Equal(flow.Nodes.Select(n => n.Id), moved.Nodes.Select(n => n.Id));
    }
}
