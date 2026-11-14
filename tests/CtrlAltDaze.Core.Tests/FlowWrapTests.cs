using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Core.Tests;

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

    private static void AssertNoOverlap(FlowArrangement a)
    {
        var top = a.Boxes.Where(x => x.Card is null).ToList();
        for (var i = 0; i < top.Count; i++)
        {
            for (var j = i + 1; j < top.Count; j++)
                Assert.False(top[i].Bounds.Intersects(top[j].Bounds), $"{top[i].Id} overlaps {top[j].Id}");
        }
    }

    [Fact]
    public void ATenStepChainWrapsIntoRowsOfFiveAndWhatFollowsStartsANewLineUnderIt()
    {
        // x forks, so it ends the run and starts the next line (D-066).
        var flow = FlowOf([.. Chain(10), Step("x", "s10"), Step("y", "x"), Step("z", "x")]);

        var a = Arrange(flow);

        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(B(a, "s1").Y, B(a, $"s{i}").Y);
            Assert.Equal(B(a, $"s{i}").X, B(a, $"s{i + 5}").X);
            Assert.Equal(B(a, "s1").Bottom + 48, B(a, $"s{i + 5}").Y);
        }
        Assert.Equal(B(a, "s1").X, B(a, "x").X);
        Assert.Equal(B(a, "s10").Bottom + 48, B(a, "y").Y);
        Assert.Equal(B(a, "x").CenterY, (B(a, "y").Y + B(a, "z").Bottom) / 2, 3);
        Assert.Equal(B(a, "x").Right + 64, B(a, "y").X);
        // Columns stay layers, so keyboard order and Tidy up are untouched.
        Assert.Equal(12, a.Columns.Count); // s1…s10, x, y+z
        Assert.Equal(5, a.Find("s6")!.Column);
        Assert.Equal(B(a, "s5").Right + 48, a.Extent.Width);
        Assert.Equal(B(a, "z").Bottom + 48, a.Extent.Height);
        AssertNoOverlap(a);
    }

    [Fact]
    public void ASevenChainThenAForkContinuesUnderTheBlockAtItsLeftEdge()
    {
        var flow = FlowOf([.. Chain(7), Step("x", "s7"), Step("y", "x"), Step("z", "x")]);

        var a = Arrange(flow);

        // s1…s5 | s6 s7, then x on a new line under the block, at its x.
        Assert.Equal(B(a, "s1").X, B(a, "s6").X);
        Assert.Equal(B(a, "s1").X, B(a, "x").X);
        var blockBottom = B(a, "s7").Bottom;
        Assert.Equal(blockBottom + 48, Math.Min(B(a, "x").Y, B(a, "y").Y));
        Assert.True(B(a, "x").Y > blockBottom);
        // The new line is centred among itself.
        Assert.Equal(B(a, "x").CenterY, (B(a, "y").Y + B(a, "z").Bottom) / 2, 3);
        // The block is wider than the fork, so the flow is exactly as wide as the block.
        Assert.Equal(B(a, "s5").Right + 48, a.Extent.Width);
        AssertNoOverlap(a);

        // The edge into the new line runs back along the gap under the block.
        var into = Assert.Single(a.Edges, e => e.To == "x");
        Assert.Equal(blockBottom + 24, into.WrapLaneY!.Value, 3);
        Assert.Equal(2, a.Edges.Count(e => e.WrapLaneY is not null));
        var curve = EdgeCurve.Wrapped(into.Start, into.End, into.WrapLaneY.Value);
        Assert.Equal(new Point(B(a, "x").X - 6, B(a, "x").CenterY), curve.End);
        Assert.All(curve.Corners, p => Assert.True(p.X >= B(a, "x").X - 16 && p.Y <= B(a, "x").CenterY));
    }

    [Fact]
    public void TwoBlocksInARowEachStartTheNextLine()
    {
        // Block 1 (s1…s7), then x forks to y and z, j joins them, block 2 (t1…t7), then e forks to f and g.
        var flow = FlowOf([
            .. Chain(7), Step("x", "s7"), Step("y", "x"), Step("z", "x"), Step("j", "y", "z"),
            .. Chain(7, "t", "j"), Step("e", "t7"), Step("f", "e"), Step("g", "e")]);

        var a = Arrange(flow);

        // Line 2: x … j under block 1, block 2 on that line after j, centred like any column.
        Assert.Equal(B(a, "s1").X, B(a, "x").X);
        Assert.Equal(B(a, "s7").Bottom + 48, B(a, "y").Y);
        Assert.Equal(B(a, "j").Right + 64, B(a, "t1").X);
        Assert.Equal(B(a, "j").CenterY, B(a, "t1").CenterY, 3);
        Assert.Equal(B(a, "t1").X, B(a, "t6").X);
        // Line 3: e under block 2, at its x.
        Assert.Equal(B(a, "t1").X, B(a, "e").X);
        Assert.Equal(B(a, "t7").Bottom + 48, B(a, "f").Y);
        Assert.Equal(
            ["s5→s6", "s7→x", "t5→t6", "t7→e"],
            a.Edges.Where(e => e.WrapLaneY is not null).Select(e => $"{e.From}→{e.To}").Order());
        Assert.Equal(B(a, "t5").Right + 48, a.Extent.Width);
        Assert.Equal(B(a, "g").Bottom + 48, a.Extent.Height);
        AssertNoOverlap(a);
    }

    [Fact]
    public void AColumnTallerThanTheBlockBeforeItOverlapsNeitherTheBlockNorTheNextLine()
    {
        // a forks to b…e (a column taller than the block); e → s1…s7 wraps; x after s7 forks to y and z.
        var flow = FlowOf([
            Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "a"), Step("d2", "a"), Step("d3", "a"), Step("e", "a"),
            .. Chain(7, "s", "e"), Step("x", "s7"), Step("y", "x"), Step("z", "x")]);

        var a = Arrange(flow);

        Assert.True(B(a, "e").Bottom > B(a, "s7").Bottom + 48, "the column reaches below the next line's top");
        Assert.Equal(B(a, "s1").X, B(a, "x").X);
        Assert.True(B(a, "e").Right < B(a, "x").X - 16, "the next line and its lane stay right of the column");
        AssertNoOverlap(a);
    }

    [Fact]
    public void ABlockLastEndsTheFlow()
    {
        // a forks to b and c, j joins them, then a 7-chain ends the flow.
        var flow = FlowOf([Step("a"), Step("b", "a"), Step("c", "a"), Step("j", "b", "c"), .. Chain(7, "s", "j")]);

        var a = Arrange(flow);

        Assert.Equal(B(a, "j").Right + 64, B(a, "s1").X);
        Assert.Equal(B(a, "j").CenterY, B(a, "s1").CenterY, 3);
        Assert.Equal(B(a, "s1").Bottom + 48, B(a, "s6").Y);
        Assert.Equal(["s5→s6"], a.Edges.Where(e => e.WrapLaneY is not null).Select(e => $"{e.From}→{e.To}"));
        Assert.Equal(B(a, "s5").Right + 48, a.Extent.Width);
        Assert.Equal(Math.Max(B(a, "s7").Bottom, B(a, "c").Bottom) + 48, a.Extent.Height);
        AssertNoOverlap(a);
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
        // Only the direct edge f → j detours round the arm (D-111); no chain edge is a wrap.
        Assert.DoesNotContain(a.Edges.Where(e => e.From != "f" || e.To != "j"), e => e.WrapLaneY is not null);
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

    /// <summary>check → restore → show message, plus check → show message: the long edge detours round restore (D-111).</summary>
    [Theory]
    [InlineData(LayoutLines.Curved)]
    [InlineData(LayoutLines.RightAngles)]
    public void LongEdgeDetoursRoundTheStepItSkips(LayoutLines lines)
    {
        var flow = FlowOf(
            Step("check"),
            new StepNode("restore", "wait") { After = [new Edge("check", EdgeWhen.Failure)] },
            Step("show", "restore") with { After = [new Edge("restore"), new Edge("check")] });
        var a = FlowLayout.Arrange(flow, _ => Pill, LayoutOptions.Default with { Lines = lines });
        AssertNoOverlap(a);
        var skip = a.Edges.Single(e => e.From == "check" && e.To == "show");
        Assert.NotNull(skip.WrapLaneY);
        var restore = B(a, "restore");
        Assert.True(skip.WrapLaneY < restore.Y || skip.WrapLaneY > restore.Bottom);
        Assert.All(a.Edges.Where(e => e.From != "check" || e.To != "show"), e => Assert.Null(e.WrapLaneY));
        var curve = EdgeCurve.For(skip, lines);
        for (var t = 0.0; t <= 1; t += 0.02)
            Assert.False(restore.Contains(curve.At(t)), $"strikes through restore at t={t}");
        Assert.True(a.Extent.Height >= skip.WrapLaneY + LayoutOptions.Default.Padding - 1);
    }

    [Fact]
    public void LongEdgeWithNothingInTheWayStaysACurve()
    {
        // a → b → c and a → c with b in another row: the straight line is clear only if b is off it; here b is on it.
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("d", "c"));
        var a = Arrange(flow);
        Assert.All(a.Edges, e => Assert.Null(e.WrapLaneY));
    }
}
