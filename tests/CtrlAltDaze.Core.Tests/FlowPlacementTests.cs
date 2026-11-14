using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Core.Tests;

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

    /// <summary>
    /// The user's flow (D-124): connect runs only if check fails, and set waits for both. Hung under connect, the line
    /// from check ran straight down through connect.
    /// </summary>
    private static Flow CheckConnectSet() => FlowOf(
        Step("check"),
        new StepNode("connect", "wait") { After = [new Edge("check", EdgeWhen.Failure)] },
        Step("set", "check", "connect"));

    /// <summary>Every line, drawn Curved and Right angles, keeps out of every step but its own two.</summary>
    private static void NoLineCrossesAStep(FlowArrangement a)
    {
        foreach (var lines in new[] { LayoutLines.Curved, LayoutLines.RightAngles })
        {
            foreach (var e in a.Edges)
            {
                var curve = EdgeCurve.For(e, lines);
                foreach (var box in a.Boxes.Where(b => b.Card is null && b.Id != e.From && b.Id != e.To))
                {
                    var r = box.Bounds;
                    for (var i = 0; i <= 200; i++)
                    {
                        var p = curve.At(i / 200.0);
                        Assert.False(p.X > r.X && p.X < r.Right && p.Y > r.Y && p.Y < r.Bottom,
                            $"{lines}: {e.From} → {e.To} crosses {box.Id} at ({p.X:0}, {p.Y:0})");
                    }
                }
            }
        }
    }

    /// <summary>The join cases every shape must draw with no line through a step (D-124).</summary>
    public static TheoryData<LayoutShape, string> JoinCases()
    {
        var data = new TheoryData<LayoutShape, string>();
        foreach (var shape in Enum.GetValues<LayoutShape>())
        {
            foreach (var name in JoinFlows.Keys)
                data.Add(shape, name);
        }
        return data;
    }

    private static readonly Dictionary<string, Func<Flow>> JoinFlows = new()
    {
        ["check-connect-set"] = CheckConnectSet,
        ["diamond"] = Diamond,
        ["skip-two-levels"] = () => FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("j", "a", "c"), Step("k", "j")),
        ["skip-with-sibling"] = () => FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("e", "b"), Step("j", "a", "c")),
        ["fan-in-of-three"] = () => FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("d", "c"), Step("j", "a", "b", "c", "d")),
        ["three-in-one"] = () => FlowOf(Step("a"), Step("b"), Step("c"), Step("j", "a", "b", "c"), Step("k", "j")),
        ["fork-three-join"] = () => FlowOf(Step("s"), Step("a", "s"), Step("b", "s"), Step("c", "s"), Step("j", "a", "b", "c")),
    };

    [Theory]
    [MemberData(nameof(JoinCases))]
    public void LinesKeepClearOfStepsAndLanesAreNotShared(LayoutShape shape, string flow)
    {
        foreach (var lines in new[] { LayoutLines.Curved, LayoutLines.RightAngles })
        {
            var a = FlowPlacement.Arrange(JoinFlows[flow](), _ => Pill, LayoutOptions.Default with { Shape = shape, Lines = lines });
            foreach (var e in a.Edges)
            {
                var curve = EdgeCurve.For(e, lines);
                foreach (var box in a.Boxes.Where(b => b.Card is null && b.Id != e.From && b.Id != e.To))
                {
                    var r = box.Bounds;
                    for (var i = 0; i <= 200; i++)
                    {
                        var p = curve.At(i / 200.0);
                        var dx = Math.Max(Math.Max(r.X - p.X, p.X - r.Right), 0);
                        var dy = Math.Max(Math.Max(r.Y - p.Y, p.Y - r.Bottom), 0);
                        Assert.True(Math.Sqrt(dx * dx + dy * dy) >= FlowPlacement.LineClearance - 0.01,
                            $"{lines}: {e.From} → {e.To} runs {Math.Sqrt(dx * dx + dy * dy):0.0} DIP from {box.Id}");
                    }
                }
            }
            // No two detours run along one lane over the same stretch (F-040).
            var runs = a.Edges.Where(e => e.Lane is not null).Select(e =>
            {
                var c = EdgeCurve.For(e, lines).Corners; // [start, out, lane start, lane end, in, end]
                var vertical = c[2].X == c[3].X;
                var (from, to) = vertical ? (c[2].Y, c[3].Y) : (c[2].X, c[3].X);
                return (e, Lane: e.Lane!.Value, Lo: Math.Min(from, to), Hi: Math.Max(from, to), Vertical: vertical);
            }).ToList();
            foreach (var x in runs)
            {
                Assert.DoesNotContain(runs, y => !ReferenceEquals(x.e, y.e) && x.Vertical == y.Vertical && Math.Abs(x.Lane - y.Lane) < 1
                    && x.Lo < y.Hi && y.Lo < x.Hi);
            }
        }
    }

    [Fact]
    public void AWideJoinTopToBottomNestsItsLanesLaneGapApart()
    {
        var a = Arrange(JoinFlows["fan-in-of-three"](), LayoutShape.TopToBottom);
        var lanes = a.Edges.Where(e => e.Lane is not null).OrderBy(e => e.From, StringComparer.Ordinal).Select(e => e.Lane!.Value).ToList();

        Assert.Equal(3, lanes.Count); // a, b and c each skip to j round the steps under them
        Assert.Equal(lanes.Order().Reverse(), lanes); // the longest line outermost: nested, no crossing
        Assert.Equal(FlowPlacement.LaneGap, lanes[0] - lanes[1]);
        Assert.Equal(FlowPlacement.LaneGap, lanes[1] - lanes[2]);
    }

    [Theory]
    [MemberData(nameof(JoinCases))]
    public void NoShapeDrawsALineThroughAStep(LayoutShape shape, string flow)
    {
        var a = Arrange(JoinFlows[flow](), shape);

        NoLineCrossesAStep(a);
        NoOverlaps(a);
        Assert.Equal(a, Arrange(JoinFlows[flow](), shape), new ArrangementComparer()); // the same picture each time
        Assert.All(a.Boxes, b => Assert.True(b.Bounds.X >= 0 && b.Bounds.Y >= 0 && b.Bounds.Right <= a.Extent.Width && b.Bounds.Bottom <= a.Extent.Height));
    }

    [Fact]
    public void LinesBetweenStepsPlacedByHandGoRoundTheStepsBetween()
    {
        // Placed in one column by hand: check's line to set would run straight down through connect.
        var placed = new Dictionary<string, Point> { ["check"] = new(48, 48), ["connect"] = new(48, 200), ["set"] = new(48, 352) };
        var a = Arrange(CheckConnectSet(), LayoutShape.Tree, placed);

        Assert.Equal((48.0, 200.0), (B(a, "connect").X, B(a, "connect").Y)); // nothing moves
        NoLineCrossesAStep(a);
    }

    [Fact]
    public void TreePutsAJoinThatSkipsALevelBesideItsStepsCentredBetweenThem()
    {
        var a = Arrange(CheckConnectSet(), LayoutShape.Tree);

        Assert.Equal(CentreX(B(a, "check")), CentreX(B(a, "connect")), 3); // connect stays under check
        Assert.True(B(a, "connect").Y > B(a, "check").Bottom);
        Assert.True(B(a, "set").X > Math.Max(B(a, "check").Right, B(a, "connect").Right)); // set beside them
        Assert.Equal((B(a, "check").CenterY + B(a, "connect").CenterY) / 2, B(a, "set").CenterY, 3);
        foreach (var e in a.Edges.Where(e => e.To == "set"))
            Assert.Equal((PortSide.Right, PortSide.Left), (e.StartSide, e.EndSide)); // a fan-in from the side
        var down = a.Edges.Single(e => e.To == "connect");
        Assert.Equal((PortSide.Bottom, PortSide.Top), (down.StartSide, down.EndSide));
        NoLineCrossesAStep(a);
        NoOverlaps(a);
    }

    [Fact]
    public void TreePutsAJoinBesideALongerChainAndKeepsItsOwnBranchUnderIt()
    {
        // a → b → c → j, a → j skips two levels; j leads on to k.
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("j", "a", "c"), Step("k", "j"));
        var a = Arrange(flow, LayoutShape.Tree);

        Assert.Equal(CentreX(B(a, "a")), CentreX(B(a, "c")), 3);
        Assert.True(B(a, "j").X > B(a, "b").Right);
        Assert.Equal((B(a, "a").CenterY + B(a, "c").CenterY) / 2, B(a, "j").CenterY, 3);
        Assert.Equal(CentreX(B(a, "j")), CentreX(B(a, "k")), 3);
        Assert.True(B(a, "k").Y > B(a, "j").Bottom);
        NoLineCrossesAStep(a);
        NoOverlaps(a);
    }

    [Fact]
    public void TreeDiamondStaysATreeWithClearLines()
    {
        var a = Arrange(Diamond(), LayoutShape.Tree);

        Assert.Equal(B(a, "b").Y, B(a, "c").Y);
        Assert.Equal(CentreX(B(a, "b")), CentreX(B(a, "d")), 3); // hung under b, as before
        Assert.True(B(a, "d").Y > B(a, "c").Bottom);
        NoLineCrossesAStep(a);
        NoOverlaps(a);
    }

    [Fact]
    public void TreeWithAJoinNoPlaceClearsStillLaysOutTheSameEachTime()
    {
        // b's other child e sits right of c, so a line from c to a join beside the branch cuts through e: the tree keeps
        // whichever place crosses fewer steps (here beside), and stays deterministic.
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("e", "b"), Step("j", "a", "c"));
        var a = Arrange(flow, LayoutShape.Tree);

        NoOverlaps(a);
        Assert.Equal(a, Arrange(flow, LayoutShape.Tree), new ArrangementComparer());
    }

    [Theory]
    [InlineData(LayoutShape.Tree)]
    [InlineData(LayoutShape.TopToBottom)]
    [InlineData(LayoutShape.Circle)]
    [InlineData(LayoutShape.LeftToRight)]
    public void ArrangingAgainGivesTheSamePicture(LayoutShape shape)
    {
        foreach (var flow in new[] { CheckConnectSet(), Diamond(), Fan(), Chain(5) })
            Assert.Equal(Arrange(flow, shape), Arrange(flow, shape), new ArrangementComparer());
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
    public void CircleKeepsABranchTogetherRoundTheRing()
    {
        // a forks to b and c; b goes on to d. Round the ring: a, b, d, c (not column by column: a, b, c, d).
        var a = Arrange(FlowOf(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b")), LayoutShape.Circle);
        var centre = new Point(a.Boxes.Average(x => CentreX(x.Bounds)), a.Boxes.Average(x => x.Bounds.CenterY));
        double Angle(string id) => (Math.Atan2(B(a, id).CenterY - centre.Y, CentreX(B(a, id)) - centre.X) + 2.5 * Math.PI) % (2 * Math.PI);

        List<string> ids = ["a", "b", "c", "d"];
        Assert.Equal(["a", "b", "d", "c"], ids.OrderBy(Angle));
    }

    [Fact]
    public void CircleLeavesRoomForATallCard()
    {
        var members = Enumerable.Range(1, 12).Select(i => $"m{i}").ToList();
        var flow = FlowOf([Step("a"), Step("b", "a"), new GroupNode("g", [.. members]) { After = [new Edge("b")] }, Step("c", "g"),
            .. members.Select(m => (Node)Step(m))]);
        var a = Arrange(flow, LayoutShape.Circle);

        Assert.True(B(a, "g").Height > 600);
        NoOverlaps(a);
    }

    [Fact]
    public void PlacesLeftByDeletedStepsDontCount()
    {
        var flow = Chain(8);
        var stale = new Dictionary<string, Point> { ["gone"] = new(400, 400) };

        Assert.False(FlowPlacement.HasPlaced(flow, stale));
        Assert.Equal(FlowLayout.Arrange(flow, _ => Pill), Arrange(flow, placed: stale), new ArrangementComparer());
    }

    [Theory]
    [InlineData(LayoutShape.TopToBottom)]
    [InlineData(LayoutShape.Tree)]
    [InlineData(LayoutShape.Circle)]
    public void AnEmptyFlowHasNothingToArrange(LayoutShape shape)
    {
        var a = Arrange(FlowOf(), shape);

        Assert.Empty(a.Boxes);
        Assert.Empty(a.Edges);
        Assert.Empty(Arrange(FlowOf(), shape, new Dictionary<string, Point> { ["gone"] = new(96, 96) }).Boxes);
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
