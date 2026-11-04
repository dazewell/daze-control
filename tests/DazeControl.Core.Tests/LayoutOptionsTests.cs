using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

/// <summary>The Layout menu's choices (D-067): wrap, spacing, line up, lines, untangle.</summary>
public class LayoutOptionsTests
{
    private static readonly Size Pill = new(232, 56);

    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static Flow Chain(int count) =>
        FlowOf([.. Enumerable.Range(1, count).Select(i => (Node)(i == 1 ? Step("s1") : Step($"s{i}", $"s{i - 1}")))]);

    /// <summary>a forks into b and c, which join into d.</summary>
    private static Flow Diamond() => FlowOf(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b", "c"));

    private static FlowArrangement Arrange(Flow flow, LayoutOptions options) => FlowLayout.Arrange(flow, _ => Pill, options);

    private static Rect B(FlowArrangement a, string id) => a.Find(id)!.Bounds;

    private static LayoutOptions Options(int wrapAfter = 5, LayoutSpacing spacing = LayoutSpacing.Normal,
        LayoutAlign align = LayoutAlign.Centre, LayoutLines lines = LayoutLines.Curved) =>
        LayoutOptions.Of(wrapAfter, spacing, align, lines);

    [Fact]
    public void TheDefaultChoicesAreTheDefaultLayout()
    {
        Assert.Equal(LayoutOptions.Default, Options());
    }

    [Theory]
    [InlineData(LayoutSpacing.Compact, 40, 8)]
    [InlineData(LayoutSpacing.Normal, 64, 16)]
    [InlineData(LayoutSpacing.Roomy, 96, 32)]
    public void SpacingSetsTheGapsBetweenColumnsAndBetweenBoxes(LayoutSpacing spacing, double column, double row)
    {
        var a = Arrange(Diamond(), Options(spacing: spacing));

        Assert.Equal(B(a, "a").Right + column, B(a, "b").X);
        Assert.Equal(B(a, "b").Bottom + row, B(a, "c").Y);
        Assert.Equal(B(a, "b").Right + column, B(a, "d").X);
    }

    [Fact]
    public void RoomyIsWiderAndTallerThanCompact()
    {
        var compact = Arrange(Diamond(), Options(spacing: LayoutSpacing.Compact));
        var roomy = Arrange(Diamond(), Options(spacing: LayoutSpacing.Roomy));

        Assert.True(roomy.Extent.Width > compact.Extent.Width);
        Assert.True(roomy.Extent.Height > compact.Extent.Height);
    }

    [Theory]
    [InlineData(3, 4)] // 3 + 3 + 3 + 1
    [InlineData(5, 2)]
    [InlineData(8, 2)] // 8 + 2
    [InlineData(0, 1)] // Never
    public void WrapLongChainsAfterNStepsOrNever(int wrapAfter, int rows)
    {
        var a = Arrange(Chain(10), Options(wrapAfter));

        Assert.Equal(rows, a.Boxes.Select(b => b.Bounds.Y).Distinct().Count());
        if (wrapAfter > 0)
        {
            Assert.Equal(B(a, "s1").Y, B(a, $"s{wrapAfter}").Y);
            Assert.Equal(B(a, "s1").X, B(a, $"s{wrapAfter + 1}").X);
            Assert.Equal(B(a, "s1").Bottom + 48, B(a, $"s{wrapAfter + 1}").Y);
        }
        Assert.Equal(rows - 1, a.Edges.Count(e => e.WrapLaneY is not null));
    }

    [Fact]
    public void AChainNoLongerThanWrapAfterStaysOnOneRow()
    {
        var a = Arrange(Chain(8), Options(8));

        Assert.Single(a.Boxes.Select(b => b.Bounds.Y).Distinct());
    }

    [Fact]
    public void CentredColumnsMeetOnTheirMiddle()
    {
        var a = Arrange(Diamond(), Options());

        Assert.Equal(B(a, "a").CenterY, (B(a, "b").Y + B(a, "c").Bottom) / 2, 3);
        Assert.Equal(B(a, "a").CenterY, B(a, "d").CenterY, 3);
    }

    [Fact]
    public void AlongTheTopHangsEveryColumnFromTheLinesTop()
    {
        var a = Arrange(Diamond(), Options(align: LayoutAlign.Top));

        Assert.Equal(48, B(a, "a").Y);
        Assert.Equal(48, B(a, "b").Y);
        Assert.Equal(48, B(a, "d").Y);
        Assert.Equal(B(a, "b").Bottom + 16, B(a, "c").Y);
    }

    [Fact]
    public void AlongTheTopAlsoLinesUpAWrappedChainAndTheLineAfterIt()
    {
        // A tall card starts the chain (card, s1…s4 | s5…s7): centred, the pills sit at the card's middle.
        var flow = FlowOf([
            new GroupNode("together", ["m1", "m2", "m3"]), Step("m1"), Step("m2"), Step("m3"),
            .. Enumerable.Range(1, 7).Select(i => (Node)Step($"s{i}", i == 1 ? "together" : $"s{i - 1}")),
        ]);

        var centred = Arrange(flow, Options());
        var top = Arrange(flow, Options(align: LayoutAlign.Top));

        Assert.True(B(centred, "s1").Y > B(centred, "together").Y);
        Assert.Equal(B(top, "together").Y, B(top, "s1").Y);
        Assert.Equal(B(top, "together").Bottom + 48, B(top, "s5").Y);
    }

    [Fact]
    public void RightAnglesMakeEveryEdgeOrthogonalThroughTheGapsMiddle()
    {
        var a = Arrange(Diamond(), Options(lines: LayoutLines.RightAngles));
        var route = a.Edges.Single(e => e.From == "a" && e.To == "c");

        var curve = EdgeCurve.For(route, LayoutLines.RightAngles);

        Assert.True(curve.IsOrthogonal);
        Assert.False(curve.IsWrapped);
        var corners = curve.Corners;
        Assert.Equal(4, corners.Count);
        for (var i = 1; i < corners.Count; i++)
            Assert.True(corners[i - 1].X == corners[i].X || corners[i - 1].Y == corners[i].Y, $"segment {i} is diagonal");
        var bend = (route.Start.X + route.End.X - EdgeCurve.ArrowLength) / 2;
        Assert.Equal(bend, curve.BendX!.Value, 3);
        Assert.Equal(new Point(bend, (route.Start.Y + route.End.Y) / 2), curve.Midpoint);
        Assert.True(curve.DistanceTo(curve.Midpoint) < 0.01);
        Assert.True(curve.DistanceTo(new Point(bend, route.Start.Y)) < 0.01);
        Assert.True(curve.DistanceTo(curve.Tip) < 0.01);
    }

    [Fact]
    public void CurvedLinesStayBeziersAndAWrappedEdgeStaysWrappedEitherWay()
    {
        var a = Arrange(Chain(7), Options());
        var plain = a.Edges.Single(e => e.From == "s1");
        var wrapped = a.Edges.Single(e => e.WrapLaneY is not null);

        Assert.False(EdgeCurve.For(plain, LayoutLines.Curved).IsOrthogonal);
        Assert.True(EdgeCurve.For(wrapped, LayoutLines.Curved).IsWrapped);
        Assert.True(EdgeCurve.For(wrapped, LayoutLines.RightAngles).IsWrapped);
        Assert.Null(EdgeCurve.For(wrapped, LayoutLines.RightAngles).BendX);
    }

    [Fact]
    public void ARightAngledEdgeIntoATargetBehindItBendsOneStepOut()
    {
        var curve = EdgeCurve.RightAngled(new Point(300, 0), new Point(100, 80));

        Assert.Equal(300 + EdgeCurve.WrapStep, curve.BendX);
    }

    [Fact]
    public void UntangleIsOffOnAChainBecauseNoLinesCross()
    {
        Assert.Equal(UntangleState.NoCrossings, FlowTidy.Check(Chain(7)));
        Assert.Equal(UntangleState.NoCrossings, FlowTidy.Check(Diamond(), LayoutAlign.Top));
    }

    [Fact]
    public void UntangleHelpsWhenAnotherOrderHasFewerCrossings()
    {
        var flow = FlowOf(Step("a"), Step("b"), Step("x", "b"), Step("y", "a"));

        Assert.Equal(UntangleState.Helps, FlowTidy.Check(flow));
    }

    [Fact]
    public void UntangleIsOffWhenNoOrderCrossesLess()
    {
        // Two sources each feeding both targets: one crossing in every order.
        var flow = FlowOf(Step("a"), Step("b"), Step("x", "a", "b"), Step("y", "a", "b"));

        Assert.Equal(1, FlowTidy.Crossings(flow));
        Assert.Equal(UntangleState.NoBetterOrder, FlowTidy.Check(flow));
    }
}
