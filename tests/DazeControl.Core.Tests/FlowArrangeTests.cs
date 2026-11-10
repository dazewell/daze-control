using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

/// <summary>Arrange › Tree / Circle and steps placed by hand (D-115).</summary>
public class FlowArrangeTests
{
    private static readonly Size Pill = new(232, 56);

    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static FlowArrangement Arrange(Flow flow, LayoutOptions options) => FlowLayout.Arrange(flow, _ => Pill, options);

    private static LayoutOptions Shape(LayoutShape shape) => LayoutOptions.Default with { Shape = shape };

    private static LayoutOptions Pinned(string id, double x, double y) =>
        LayoutOptions.Default with { Pins = new Dictionary<string, Point> { [id] = new(x, y) } };

    private static void AssertNoOverlap(FlowArrangement arrangement)
    {
        var top = arrangement.Boxes.Where(b => b.Card is null).ToList();
        for (var i = 0; i < top.Count; i++)
        {
            for (var j = i + 1; j < top.Count; j++)
                Assert.False(top[i].Bounds.Intersects(top[j].Bounds), $"{top[i].Id} overlaps {top[j].Id}");
        }
    }

    private static Flow Branchy() => FlowOf(
        Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b"), Step("e", "b"), Step("f", "c"), Step("g", "d", "f"), Step("h"));

    [Fact]
    public void TreeCentresAStepOnTheStepsThatWaitForIt()
    {
        var tree = Arrange(Branchy(), Shape(LayoutShape.Tree));

        var b = tree.Find("b")!.Bounds;
        Assert.Equal((tree.Find("d")!.Bounds.CenterY + tree.Find("e")!.Bounds.CenterY) / 2, b.CenterY, 3);
        Assert.Equal((b.CenterY + tree.Find("c")!.Bounds.CenterY) / 2, tree.Find("a")!.Bounds.CenterY, 3);
        Assert.Equal(tree.Find("c")!.Bounds.CenterY, tree.Find("f")!.Bounds.CenterY, 3); // an only branch sits beside its step
        AssertNoOverlap(tree);
    }

    [Fact]
    public void TreeKeepsTheColumnsSoEveryLineStillRunsLeftToRight()
    {
        var tree = Arrange(Branchy(), Shape(LayoutShape.Tree));
        var columns = Arrange(Branchy(), LayoutOptions.Default);

        foreach (var box in tree.Boxes)
            Assert.Equal(columns.Find(box.Id)!.Bounds.X, box.Bounds.X);
        Assert.All(tree.Edges, e => Assert.True(e.End.X > e.Start.X && !e.IsFloating));
        Assert.True(tree.Find("h")!.Bounds.Y > tree.Find("a")!.Bounds.Bottom); // a second start goes under the first tree
    }

    [Fact]
    public void TreeTakesFractionalUnequalHeights()
    {
        // 225 % text: measured heights aren't whole, and a band's far edge can round a hair under its top.
        var heights = new Dictionary<string, double> { ["lone"] = 57.6, ["root"] = 76.8, ["child"] = 56, ["tall"] = 133.3, ["leaf"] = 19.7 };
        var flow = FlowOf(Step("lone"), Step("root"), Step("child", "root"), Step("tall"), Step("leaf", "tall"));

        var tree = FlowLayout.Arrange(flow, id => new Size(232, heights[id]), Shape(LayoutShape.Tree));

        AssertNoOverlap(tree);
        Assert.Equal(tree.Find("root")!.Bounds.CenterY, tree.Find("child")!.Bounds.CenterY, 3);
        Assert.Equal(tree.Find("tall")!.Bounds.CenterY, tree.Find("leaf")!.Bounds.CenterY, 3);
    }

    [Fact]
    public void TreeSurvivesAHalfBuiltFlowWithACycle()
    {
        var tree = Arrange(FlowOf(Step("a", "c"), Step("b", "a"), Step("c", "b"), Step("d", "nope")), Shape(LayoutShape.Tree));

        Assert.Equal(4, tree.Boxes.Count);
        AssertNoOverlap(tree);
    }

    [Theory]
    [InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7), InlineData(9), InlineData(12), InlineData(20)]
    public void CircleNeverOverlapsAndStaysOnTheCanvas(int count)
    {
        var flow = FlowOf([.. Enumerable.Range(0, count).Select(i => i == 0 ? Step("s0") : Step($"s{i}", $"s{i - 1}"))]);

        foreach (var spacing in Enum.GetValues<LayoutSpacing>())
        {
            var options = LayoutOptions.Of(5, spacing, LayoutAlign.Centre, LayoutLines.Curved) with { Shape = LayoutShape.Circle };
            var circle = Arrange(flow, options);

            AssertNoOverlap(circle);
            Assert.All(circle.Boxes, b => Assert.True(b.Bounds.X >= options.Padding - 0.001 && b.Bounds.Y >= options.Padding - 0.001));
            Assert.All(circle.Boxes, b => Assert.True(b.Bounds.Right <= circle.Extent.Width && b.Bounds.Bottom <= circle.Extent.Height));
        }
    }

    [Fact]
    public void CircleStartsOnTheLeftAndGoesClockwiseWithLinesBetweenFacingSides()
    {
        var circle = Arrange(FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("d", "c")), Shape(LayoutShape.Circle));

        var (a, b, c, d) = (circle.Find("a")!.Bounds, circle.Find("b")!.Bounds, circle.Find("c")!.Bounds, circle.Find("d")!.Bounds);
        Assert.True(a.X < b.X && b.X < c.X); // left, top, right
        Assert.True(b.Y < a.Y && d.Y > a.Y); // top above, the last at the bottom
        var back = circle.Edges.Single(e => e.To == "d"); // right → bottom runs back to the left
        Assert.Equal((PortSide.Left, PortSide.Right), (back.Leaves, back.Enters));
        Assert.Equal(new Point(c.X, c.CenterY), back.Start);
        Assert.Equal(new Point(d.Right, d.CenterY), back.End);
    }

    [Fact]
    public void APlacedStepSitsWhereItWasPutAndNothingElseMoves()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"));
        var auto = Arrange(flow, LayoutOptions.Default);

        var placed = Arrange(flow, Pinned("b", 400, 320));

        Assert.Equal(new Rect(400, 320, Pill.Width, Pill.Height), placed.Find("b")!.Bounds);
        Assert.Equal(auto.Find("a")!.Bounds, placed.Find("a")!.Bounds);
        Assert.Equal(auto.Find("c")!.Bounds, placed.Find("c")!.Bounds);
        Assert.True(placed.Extent.Height >= 320 + Pill.Height);
        Assert.Equal(auto.Columns, placed.Columns); // keyboard order and "run after" are the flow's, not the picture's
    }

    [Fact]
    public void LinesToAPlacedStepUseTheSidesThatFaceEachOther()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"));
        var a = Arrange(flow, LayoutOptions.Default).Find("a")!.Bounds;

        var below = Arrange(flow, Pinned("b", a.X, a.Bottom + 200)).Edges.Single(e => e.To == "b");

        Assert.Equal((PortSide.Bottom, PortSide.Top), (below.Leaves, below.Enters));
        Assert.Equal(new Point(a.X + a.Width / 2, a.Bottom), below.Start);
        var curve = EdgeCurve.For(below, LayoutLines.RightAngles);
        Assert.False(curve.IsOrthogonal);
        Assert.Equal(below.End.Y - EdgeCurve.ArrowLength, curve.End.Y); // the head points down into the top side
        Assert.Equal(below.End.X, curve.End.X);
    }

    [Fact]
    public void ALineToAPlacedStepIsACurveEvenWhenItRunsLeftToRightWithRightAngles()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"));
        var a = Arrange(flow, LayoutOptions.Default).Find("a")!.Bounds;

        var placed = Arrange(flow, Pinned("b", a.Right + 300, a.Y + 120) with { Lines = LayoutLines.RightAngles });
        var beside = placed.Edges.Single(e => e.To == "b");

        Assert.Equal((PortSide.Right, PortSide.Left), (beside.Leaves, beside.Enters));
        Assert.True(beside.IsFloating);
        Assert.False(EdgeCurve.For(beside, LayoutLines.RightAngles).IsOrthogonal);
    }

    [Fact]
    public void APlacedCardTakesItsMembersAlong()
    {
        var flow = FlowOf(Step("a"), Step("m1"), Step("m2"), new GroupNode("card", ["m1", "m2"]) { After = [new Edge("a")] });
        var auto = Arrange(flow, LayoutOptions.Default);

        var placed = Arrange(flow, Pinned("card", 600, 400));

        var card = placed.Find("card")!.Bounds;
        Assert.Equal((600, 400), (card.X, card.Y));
        foreach (var member in new[] { "m1", "m2" })
        {
            Assert.Equal(auto.Find(member)!.Bounds.X - auto.Find("card")!.Bounds.X, placed.Find(member)!.Bounds.X - card.X);
            Assert.Equal(auto.Find(member)!.Bounds.Y - auto.Find("card")!.Bounds.Y, placed.Find(member)!.Bounds.Y - card.Y);
        }
    }

    [Fact]
    public void APinForAStepThatIsGoneOrOffTheCanvasIsHarmless()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"));
        var options = LayoutOptions.Default with { Pins = new Dictionary<string, Point> { ["gone"] = new(10, 10), ["a"] = new(-50, -5) } };

        var placed = Arrange(flow, options);

        Assert.Equal((0, 0), (placed.Find("a")!.Bounds.X, placed.Find("a")!.Bounds.Y));
        Assert.Equal(2, placed.Boxes.Count);
    }
}
