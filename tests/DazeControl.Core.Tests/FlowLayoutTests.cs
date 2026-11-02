using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class FlowLayoutTests
{
    private static readonly Size Pill = new(232, 56);

    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static FlowArrangement Arrange(Flow flow) => FlowLayout.Arrange(flow, _ => Pill);

    [Fact]
    public void ColumnIsLongestPathFromAStart()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "a", "b"), Step("d"));

        var layers = FlowLayout.Layers(flow);

        Assert.Equal(0, layers["a"]);
        Assert.Equal(1, layers["b"]);
        Assert.Equal(2, layers["c"]);
        Assert.Equal(0, layers["d"]);
    }

    [Fact]
    public void ColumnsAreSpacedByTheGapAndRowsKeepNodesOrder()
    {
        var arrangement = Arrange(FlowOf(Step("b"), Step("a"), Step("c", "a")));

        Assert.Equal(["b", "a"], arrangement.Columns[0]);
        var b = arrangement.Find("b")!.Bounds;
        var a = arrangement.Find("a")!.Bounds;
        var c = arrangement.Find("c")!.Bounds;
        Assert.Equal(48, b.X);
        Assert.Equal(48, b.Y);
        Assert.Equal(b.Bottom + 16, a.Y);
        Assert.Equal(b.Right + 64, c.X);
    }

    [Fact]
    public void ShorterColumnsAreCentredOnTheTallest()
    {
        var arrangement = Arrange(FlowOf(Step("a"), Step("b"), Step("c", "a", "b")));

        var a = arrangement.Find("a")!.Bounds;
        var b = arrangement.Find("b")!.Bounds;
        var c = arrangement.Find("c")!.Bounds;
        Assert.Equal((a.Y + b.Bottom) / 2, c.CenterY, 3);
    }

    [Fact]
    public void NoBoxesOverlap()
    {
        var arrangement = Arrange(FlowOf(
            Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b", "c"), Step("e"),
            new GroupNode("g", ["m1", "m2"]) { After = [new Edge("e")] }, Step("m1"), Step("m2")));

        var top = arrangement.Boxes.Where(x => x.Card is null).ToList();
        for (var i = 0; i < top.Count; i++)
        {
            for (var j = i + 1; j < top.Count; j++)
                Assert.False(top[i].Bounds.Intersects(top[j].Bounds), $"{top[i].Id} overlaps {top[j].Id}");
        }
    }

    [Fact]
    public void CardStacksItsMembersAndTakesTheirEdges()
    {
        var flow = FlowOf(Step("a"), new GroupNode("g", ["m1", "m2"]) { After = [new Edge("a")] }, Step("m1"), Step("m2"), Step("z", "g"));

        var arrangement = Arrange(flow);

        var card = arrangement.Find("g")!;
        var m1 = arrangement.Find("m1")!;
        var m2 = arrangement.Find("m2")!;
        Assert.Equal(1, card.Column);
        Assert.Equal("g", m1.Card);
        Assert.Equal(card.Bounds.X + 12, m1.Bounds.X);
        Assert.Equal(card.Bounds.Y + 32 + 12, m1.Bounds.Y);
        Assert.Equal(m1.Bounds.Bottom + 8, m2.Bounds.Y);
        Assert.Equal(card.Bounds.Bottom - 12, m2.Bounds.Bottom);
        Assert.Equal(232 + 24, card.Bounds.Width);
        Assert.Equal(2, arrangement.Find("z")!.Column);
        Assert.Equal(["a", "g", "z"], arrangement.Columns.SelectMany(c => c));
    }

    [Fact]
    public void EdgesRunFromRightMiddleToLeftMiddle()
    {
        var arrangement = Arrange(FlowOf(Step("a"), new StepNode("b", "wait") { After = [new Edge("a", EdgeWhen.Failure)] }));

        var edge = Assert.Single(arrangement.Edges);
        var a = arrangement.Find("a")!.Bounds;
        var b = arrangement.Find("b")!.Bounds;
        Assert.Equal(EdgeWhen.Failure, edge.When);
        Assert.Equal(new Point(a.Right, a.CenterY), edge.Start);
        Assert.Equal(new Point(b.X, b.CenterY), edge.End);
    }

    [Fact]
    public void HalfBuiltFlowsStillLayOut()
    {
        // A cycle, a dangling ref, a duplicate edge and an edge onto a card member: none of them throws.
        var flow = FlowOf(Step("a", "b", "missing"), Step("b", "a", "a"), new GroupNode("g", ["m", "m", "nope"]), Step("m", "a"));

        var arrangement = Arrange(flow);

        Assert.Equal(["a", "b", "g", "m"], arrangement.Boxes.Select(b => b.Id).Order());
        Assert.Contains(arrangement.Edges, e => e.From == "a" && e.To == "g");
    }

    [Fact]
    public void WiderPillsWidenTheirColumn()
    {
        var flow = FlowOf(Step("a"), Step("wide"), Step("c", "a"));

        var arrangement = FlowLayout.Arrange(flow, id => id == "wide" ? new Size(320, 80) : Pill);

        Assert.Equal(48 + 320 + 64, arrangement.Find("c")!.Bounds.X);
        Assert.Equal(48 + 320 + 64 + 232 + 48, arrangement.Extent.Width);
    }

    [Fact]
    public void EmptyFlowHasOnlyPadding()
    {
        var arrangement = Arrange(FlowOf());

        Assert.Empty(arrangement.Boxes);
        Assert.Equal(new Size(96, 96), arrangement.Extent);
    }

    [Fact]
    public void ADuplicateCardDoesNotHideTheFirstOnesMembers()
    {
        var flow = FlowOf(
            new GroupNode("g", ["a", "b"]), new GroupNode("g", ["c", "d"]),
            Step("a"), Step("b"), Step("c"), Step("d"));

        var arrangement = Arrange(flow);

        Assert.Equal("g", arrangement.Find("a")?.Card);
        Assert.Equal("g", arrangement.Find("b")?.Card);
        Assert.Null(arrangement.Find("c")?.Card);
        Assert.NotNull(arrangement.Find("c"));
    }
}
