using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

/// <summary>Arrange shapes and hand placement (D-115).</summary>
public class FlowPlacementTests
{
    private static readonly Size Pill = new(232, 56);

    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static Flow Chain(int count) =>
        FlowOf([.. Enumerable.Range(1, count).Select(i => (Node)(i == 1 ? Step("s1") : Step($"s{i}", $"s{i - 1}")))]);

    /// <summary>a forks into b and c, which join into d.</summary>
    private static Flow Diamond() => FlowOf(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b", "c"));

    /// <summary>a fans out to b, c and d; b to e and f.</summary>
    private static Flow Fan() => FlowOf(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "a"), Step("e", "b"), Step("f", "b"));

    private static FlowArrangement Arrange(Flow flow, LayoutShape shape = LayoutShape.LeftToRight, IReadOnlyDictionary<string, Point>? placed = null) =>
        FlowPlacement.Arrange(flow, _ => Pill, LayoutOptions.Default with { Shape = shape, Placed = placed });

    private static Rect B(FlowArrangement a, string id) => a.Find(id)!.Bounds;

    private static double CentreX(Rect r) => r.X + r.Width / 2;

    private static void NoOverlaps(FlowArrangement a)
    {
        var top = a.Boxes.Where(b => b.Card is null).ToList();
        for (var i = 0; i < top.Count; i++)
        {
            for (var j = i + 1; j < top.Count; j++)
            {
                var (p, q) = (top[i].Bounds, top[j].Bounds);
                Assert.False(p.X < q.Right && q.X < p.Right && p.Y < q.Bottom && q.Y < p.Bottom, $"{top[i].Id} overlaps {top[j].Id}");
            }
        }
    }

    [Fact]
    public void LeftToRightWithNothingPlacedIsTheColumnLayout()
    {
        var flow = Chain(8);
        Assert.Equal(FlowLayout.Arrange(flow, _ => Pill), Arrange(flow), new ArrangementComparer());
    }

    [Theory]
    [InlineData(LayoutShape.TopToBottom)]
    [InlineData(LayoutShape.Tree)]
    public void VerticalShapesPutEachStepUnderTheOneItFollows(LayoutShape shape)
    {
        var a = Arrange(Chain(4), shape);

        for (var i = 2; i <= 4; i++)
        {
            Assert.True(B(a, $"s{i}").Y > B(a, $"s{i - 1}").Bottom);
            Assert.Equal(CentreX(B(a, $"s{i - 1}")), CentreX(B(a, $"s{i}")), 3);
        }
        Assert.All(a.Edges, e => Assert.Equal((PortSide.Bottom, PortSide.Top), (e.StartSide, e.EndSide)));
        Assert.All(a.Edges, e => Assert.Equal(B(a, e.From).Bottom, e.Start.Y));
        Assert.Null(a.Edges[0].WrapLaneY); // no wrapped rows outside left to right
    }

    [Fact]
    public void TreeCentresAParentOverItsChildren()
    {
        var a = Arrange(Fan(), LayoutShape.Tree);

        Assert.Equal(B(a, "b").Y, B(a, "c").Y);
        Assert.Equal(B(a, "c").Y, B(a, "d").Y);
        Assert.Equal((B(a, "e").X + B(a, "f").Right) / 2, CentreX(B(a, "b")), 3);
        Assert.Equal((B(a, "e").X + B(a, "d").Right) / 2, CentreX(B(a, "a")), 3); // b's span starts at e
        NoOverlaps(a);
    }

    [Fact]
    public void TreeHangsAJoinUnderTheStepThatRunsLast()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("j", "a", "c"));
        var a = Arrange(flow, LayoutShape.Tree);

        Assert.Equal(CentreX(B(a, "c")), CentreX(B(a, "j")), 3);
        Assert.True(B(a, "j").Y > B(a, "c").Bottom);
    }

    [Fact]
    public void TopToBottomRowsAreTheColumns()
    {
        var a = Arrange(Diamond(), LayoutShape.TopToBottom);

        Assert.Equal(B(a, "b").Y, B(a, "c").Y);
        Assert.True(B(a, "c").X > B(a, "b").Right);
        Assert.Equal(CentreX(B(a, "a")), (B(a, "b").X + B(a, "c").Right) / 2, 3);
        NoOverlaps(a);
    }

    [Fact]
    public void CircleIsARingInRunOrderClockwiseFromTheTop()
    {
        var a = Arrange(Chain(6), LayoutShape.Circle);
        var centres = Enumerable.Range(1, 6).Select(i => B(a, $"s{i}")).Select(r => new Point(CentreX(r), r.CenterY)).ToList();
        var cx = centres.Average(p => p.X);
        var cy = centres.Average(p => p.Y);
        var radii = centres.Select(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))).ToList();

        Assert.All(radii, r => Assert.Equal(radii[0], r, 1));
        Assert.Equal(centres.Min(p => p.Y), centres[0].Y); // the first step on top
        Assert.True(centres[1].X > centres[0].X); // then clockwise
        NoOverlaps(a);
        Assert.All(a.Boxes, b => Assert.True(b.Bounds.X >= LayoutOptions.Default.Padding && b.Bounds.Y >= LayoutOptions.Default.Padding));
    }

    [Fact]
    public void APlacedStepStaysWhereItWasPut()
    {
        var a = Arrange(Diamond(), placed: new Dictionary<string, Point> { ["a"] = new(400, 304), ["b"] = new(48, 48), ["c"] = new(48, 200), ["d"] = new(800, 600) });

        Assert.Equal(new Rect(400, 304, Pill.Width, Pill.Height), B(a, "a"));
        Assert.Equal(new Point(800, 600), new Point(B(a, "d").X, B(a, "d").Y));
        Assert.Equal(new Size(800 + Pill.Width + 48, 600 + Pill.Height + 48), a.Extent);
    }

    [Fact]
    public void AStepWithNoPlaceLandsBesideTheStepItRunsAfterClearOfTheRest()
    {
        var flow = FlowOf(Step("a"), Step("x"), Step("b", "a"));
        // x sits exactly where b would go: right of a, one column gap on.
        var a = Arrange(flow, placed: new Dictionary<string, Point> { ["a"] = new(48, 48), ["x"] = new(48 + Pill.Width + 64, 48) });

        Assert.Equal(B(a, "a").Right + 64, B(a, "b").X);
        Assert.True(B(a, "b").Y >= B(a, "x").Bottom);
        NoOverlaps(a);
    }

    [Fact]
    public void LinesJoinTheSidesThatFaceEachOther()
    {
        // b placed left of a and level with it: the line leaves a's left side into b's right.
        var a = Arrange(FlowOf(Step("a"), Step("b", "a")), placed: new Dictionary<string, Point> { ["a"] = new(600, 48), ["b"] = new(48, 48) });
        var e = a.Edges.Single();
        Assert.Equal((PortSide.Left, PortSide.Right), (e.StartSide, e.EndSide));
        Assert.Equal(new Point(600, 48 + Pill.Height / 2), e.Start);

        // Placed straight under it: bottom into top.
        a = Arrange(FlowOf(Step("a"), Step("b", "a")), placed: new Dictionary<string, Point> { ["a"] = new(48, 48), ["b"] = new(48, 400) });
        Assert.Equal((PortSide.Bottom, PortSide.Top), (a.Edges[0].StartSide, a.Edges[0].EndSide));
    }

    [Fact]
    public void ACardsMembersMoveWithIt()
    {
        var flow = FlowOf(Step("a"), new GroupNode("g", ["m1", "m2"]) { After = [new Edge("a")] }, Step("m1"), Step("m2"));
        var auto = Arrange(flow);
        var a = Arrange(flow, placed: new Dictionary<string, Point> { ["a"] = new(48, 48), ["g"] = new(600, 400) });

        var dx = 600 - B(auto, "g").X;
        var dy = 400 - B(auto, "g").Y;
        Assert.Equal(B(auto, "m1").X + dx, B(a, "m1").X);
        Assert.Equal(B(auto, "m2").Y + dy, B(a, "m2").Y);
        Assert.Equal("g", a.Find("m1")!.Card);
    }

    [Fact]
    public void ArrowsComeInStraightOnAnySide()
    {
        var down = EdgeCurve.Between(new Point(100, 100), new Point(100, 300), PortSide.Bottom, PortSide.Top);
        Assert.Equal(new Point(100, 300 - EdgeCurve.ArrowLength), down.End);
        Assert.True(down.Control1.Y > down.Start.Y && down.Control2.Y < down.End.Y);

        var square = EdgeCurve.RightAngled(new Point(100, 100), new Point(300, 300), PortSide.Bottom, PortSide.Top);
        Assert.Equal(200 - EdgeCurve.ArrowLength / 2, square.BendY);
        Assert.Equal([new(100, 100), new(100, 197), new(300, 197), new(300, 294)], square.Corners);

        var elbow = EdgeCurve.RightAngled(new Point(100, 100), new Point(300, 300), PortSide.Right, PortSide.Top);
        Assert.Equal(new Point(300, 100), elbow.Elbow);
        Assert.Equal(elbow.Elbow, elbow.Midpoint);
    }

    [Fact]
    public void SnapIsAnEightDipGrid()
    {
        Assert.Equal(96, FlowPlacement.Snap(99));
        Assert.Equal(104, FlowPlacement.Snap(101));
    }

    /// <summary>Arrangements hold lists, so record equality compares references; this compares contents.</summary>
    private sealed class ArrangementComparer : IEqualityComparer<FlowArrangement>
    {
        public bool Equals(FlowArrangement? x, FlowArrangement? y) =>
            x is not null && y is not null && x.Boxes.SequenceEqual(y.Boxes) && x.Edges.SequenceEqual(y.Edges) && x.Extent == y.Extent;

        public int GetHashCode(FlowArrangement obj) => obj.Boxes.Count;
    }
}
