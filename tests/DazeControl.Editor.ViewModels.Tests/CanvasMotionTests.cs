using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>
/// Layout motion (D-119 d, T21.8): the scene glides re-laid-out boxes 200 ms decelerate from where they were shown,
/// retargets from the current frame when a new layout arrives mid-glide, keeps lines on their ports every frame, fades
/// added boxes in, never animates a drag or the first layout, and does nothing with animations off.
/// </summary>
public sealed class CanvasMotionTests : IDisposable
{
    private static readonly SceneMotion On = SceneMotion.For(animationsEnabled: true);
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10); // any clock: the first frame starts the glide

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly EditorSettingsStore _store;
    private readonly FlowCanvasViewModel _canvas;

    public CanvasMotionTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _store = new EditorSettingsStore(Path.Combine(_folder, "editor.json"), _time);
        _canvas = new FlowCanvasViewModel(_session, "f", settings: _store);
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _store.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static NodeBox Box(string id, double x, double y, double w = 232, double h = 56, string? card = null) => new(id, new Rect(x, y, w, h), 0, 0, card);

    private static EdgeRoute Route(NodeBox a, NodeBox b) =>
        new(a.Id, b.Id, EdgeWhen.Success, FlowPlacement.Port(a.Bounds, PortSide.Right), FlowPlacement.Port(b.Bounds, PortSide.Left));

    private static FlowArrangement Arrangement(params NodeBox[] boxes) =>
        new(boxes, [.. boxes.Zip(boxes.Skip(1)).Select(p => Route(p.First, p.Second))], [], new Size(2000, 1000));

    private static TimeSpan Ms(double ms) => T0 + TimeSpan.FromMilliseconds(ms);

    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 6);

    // ---- Easing ----

    [Fact]
    public void DecelerateRunsFromZeroToOneFastFirst()
    {
        Assert.Equal(0, Easing.Decelerate(0));
        Assert.Equal(1, Easing.Decelerate(1));
        Assert.Equal(0, Easing.Decelerate(-1));
        Assert.Equal(1, Easing.Decelerate(2));
        Assert.Equal(0, Easing.Decelerate(double.NaN));
        var last = 0.0;
        for (var x = 0.01; x < 1; x += 0.01)
        {
            var y = Easing.Decelerate(x);
            Assert.True(y > last && y >= x, $"x={x} y={y}");
            last = y;
        }
        Near(0.5, Easing.Decelerate(0.125)); // t = 0.5: y = 3/4 − 2/8
    }

    [Fact]
    public void TheTokensComeFromUx()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), On.Reflow);
        Assert.Equal(TimeSpan.FromMilliseconds(150), On.Appear);
        Assert.True(SceneMotion.For(animationsEnabled: false).IsInstant);
    }

    // ---- Glide ----

    [Fact]
    public void AMovedBoxGlidesFromWhereItWasToWhereItIs()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 48)), motion: On);
        Assert.False(scene.IsMoving); // the first layout is shown at once

        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 248)), motion: On);
        Assert.True(scene.IsMoving);
        Assert.Equal(["b"], scene.Animated);
        Assert.Equal(48, scene.ShownBounds("b").Y); // before the first frame: where it was
        Assert.Equal(248, scene.Find("b")!.Bounds.Y); // laid out (and so UIA) at the final rect at once

        Assert.True(scene.Advance(Ms(0)));
        Assert.Equal(48, scene.ShownBounds("b").Y);
        Assert.True(scene.Advance(Ms(25))); // x = 1/8: half way already (decelerate)
        Near(148, scene.ShownBounds("b").Y);
        Assert.Equal(400, scene.ShownBounds("b").X);
        Assert.Equal(new Rect(48, 48, 232, 56), scene.ShownBounds("a"));

        Assert.False(scene.Advance(Ms(200)));
        Assert.False(scene.IsMoving);
        Assert.Equal(248, scene.ShownBounds("b").Y);
        Assert.Empty(scene.LiveCurves);
        Assert.False(scene.Advance(Ms(300))); // nothing left to do
    }

    [Fact]
    public void ANewLayoutMidGlideRetargetsFromTheCurrentFrame()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On);
        scene.Apply(Arrangement(Box("a", 48, 248)), motion: On);
        scene.Advance(Ms(0));
        scene.Advance(Ms(25));
        Near(148, scene.ShownBounds("a").Y);

        // Undo lands while it glides: back to 48, from 148, with a fresh 200 ms (no jump to either end).
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On);
        Near(148, scene.ShownBounds("a").Y);
        Assert.True(scene.Advance(Ms(40)));
        Near(148, scene.ShownBounds("a").Y); // the new glide's first frame
        Assert.True(scene.Advance(Ms(65)));
        Near(98, scene.ShownBounds("a").Y);
        Assert.False(scene.Advance(Ms(240)));
        Assert.Equal(48, scene.ShownBounds("a").Y);
    }

    [Fact]
    public void ALayoutBackToWhereItIsShownStopsTheGlide()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On);
        scene.Apply(Arrangement(Box("a", 48, 248)), motion: On);
        Assert.True(scene.IsMoving);
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On); // before any frame: shown at 48, laid out at 48
        Assert.False(scene.IsMoving);
    }

    [Fact]
    public void AnInstantLayoutOrSettleInterruptsAtTheEnd()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On);
        scene.Apply(Arrangement(Box("a", 48, 248)), motion: On);
        scene.Advance(Ms(0));
        scene.Advance(Ms(25));

        scene.Apply(Arrangement(Box("a", 48, 448))); // a layout without motion (animations off) jumps
        Assert.False(scene.IsMoving);
        Assert.Equal(448, scene.ShownBounds("a").Y);

        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On);
        scene.Advance(Ms(100));
        scene.Settle();
        Assert.False(scene.IsMoving);
        Assert.Equal(48, scene.ShownBounds("a").Y);
    }

    [Fact]
    public void AnimationsOffShowEveryLayoutAtOnce()
    {
        var scene = new CanvasScene();
        var off = SceneMotion.For(animationsEnabled: false);
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: off);
        scene.Apply(Arrangement(Box("a", 48, 248), Box("b", 400, 48)), motion: off);
        Assert.False(scene.IsMoving);
        Assert.Empty(scene.Animated);
        Assert.Equal(248, scene.ShownBounds("a").Y);
        Assert.Equal(1, scene.ShownOpacity("b"));
        Assert.Equal(1, scene.ShownScale("b"));
        Assert.False(scene.Advance(Ms(0)));
    }

    [Fact]
    public void LinesStayOnTheirPortsEveryFrame()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 48), Box("c", 800, 48)), motion: On);
        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 300), Box("c", 800, 48)), motion: On);
        Assert.Equal(2, scene.LiveCurves.Count); // both lines touch b; the canvas draws them in its live set
        foreach (var ms in new[] { 0.0, 10, 50, 120, 199 })
        {
            scene.Advance(Ms(ms));
            var b = scene.ShownBounds("b");
            Assert.Equal(FlowPlacement.Port(b, PortSide.Left), scene.ShownCurve("a", "b").Tip);
            Assert.Equal(FlowPlacement.Port(b, PortSide.Right), scene.ShownCurve("b", "c").Start);
            Assert.Equal(FlowPlacement.Port(scene.ShownBounds("a"), PortSide.Right), scene.ShownCurve("a", "b").Start);
        }
        scene.Advance(Ms(200));
        Assert.Equal(scene.FindEdge("a", "b")!.Curve, scene.ShownCurve("a", "b"));
    }

    [Fact]
    public void ALineLaneGlidesToo()
    {
        var scene = new CanvasScene();
        NodeBox a = Box("a", 48, 48), b = Box("b", 48, 300);
        FlowArrangement With(double lane) => new([a, b], [Route(a, b) with { WrapLaneY = lane }], [], new Size(1000, 600));
        scene.Apply(With(150), motion: On);
        scene.Apply(With(250), motion: On);
        Assert.True(scene.IsMoving); // no box moved, the lane did
        scene.Advance(Ms(0));
        scene.Advance(Ms(25));
        Near(200, scene.ShownCurve("a", "b").WrapLaneY!.Value);
    }

    [Fact]
    public void StepsKeepTheirNewSizeAndCardsGrowWithTheGlide()
    {
        var scene = new CanvasScene();
        var cards = new SceneSetup(Cards: new HashSet<string> { "k" });
        scene.Apply(Arrangement(Box("a", 48, 48), Box("k", 400, 48, 300, 100)), cards, On);
        scene.Apply(Arrangement(Box("a", 48, 148, w: 300), Box("k", 400, 48, 300, 200)), cards, On);
        scene.Advance(Ms(0));
        scene.Advance(Ms(25));
        Assert.Equal(300, scene.ShownBounds("a").Width); // its content set it: no stretching text
        Near(98, scene.ShownBounds("a").Y);
        Near(150, scene.ShownBounds("k").Height);
    }

    [Fact]
    public void AnAddedBoxFadesAndGrowsInButNotOnTheFirstLayout()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On);
        Assert.Equal(1, scene.ShownOpacity("a"));

        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 48)), motion: On);
        Assert.Equal(["b"], scene.Animated);
        Assert.Equal(0, scene.ShownOpacity("b"));
        Assert.Equal(MotionTokens.AppearScale, scene.ShownScale("b"));
        scene.Advance(Ms(0));
        scene.Advance(Ms(150.0 / 8)); // x = 1/8 of 150 ms
        Near(0.5, scene.ShownOpacity("b"));
        Near(0.98, scene.ShownScale("b"));

        // A layout mid-fade carries the fade on from where it is.
        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 148)), motion: On);
        Near(0.5, scene.ShownOpacity("b"));
        scene.Advance(Ms(1000)); // the new layout's first frame
        Assert.False(scene.Advance(Ms(1300)));
        Assert.Equal(1, scene.ShownOpacity("b"));
        Assert.Equal(1, scene.ShownScale("b"));
    }

    [Fact]
    public void ADropWhereTheDragShowedItDoesNotGlide()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 48)), motion: On);
        var frozen = Arrangement(Box("a", 48, 48), Box("b", 400, 48));
        scene.BeginDrag(new DragPreview(frozen, ["b"], new LayoutOptions()));
        scene.DragTo(new Point(0, 200));

        // The commit arrives while the drag is still shown: b is already there, so only what layout moved glides.
        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 248)), motion: On);
        Assert.False(scene.IsMoving);
        scene.EndDrag();
        Assert.Equal(248, scene.ShownBounds("b").Y);
    }

    [Fact]
    public void ALayoutMidDragLeavesTheDraggedBoxUnderThePointer()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48), Box("b", 400, 48)), motion: On);
        scene.BeginDrag(new DragPreview(Arrangement(Box("a", 48, 48), Box("b", 400, 48)), ["b"], new LayoutOptions()));
        scene.DragTo(new Point(0, 200));

        // a was re-measured while b is dragged: a glides, b stays where the drag shows it.
        scene.Apply(Arrangement(Box("a", 48, 96), Box("b", 400, 48)), motion: On);
        Assert.Equal(["a"], scene.Animated);
        Assert.Equal(248, scene.ShownBounds("b").Y);
    }

    [Fact]
    public void GrabbingAGlidingBoxEndsTheGlide()
    {
        var scene = new CanvasScene();
        scene.Apply(Arrangement(Box("a", 48, 48)), motion: On);
        scene.Apply(Arrangement(Box("a", 48, 248)), motion: On);
        scene.Advance(Ms(0));
        scene.Advance(Ms(25));
        scene.BeginDrag(new DragPreview(Arrangement(Box("a", 48, 248)), ["a"], new LayoutOptions()));
        Assert.False(scene.IsMoving); // direct manipulation is 1:1, never tweened
        Assert.Equal(248, scene.ShownBounds("a").Y);
    }

    // ---- The view model: which re-layouts glide ----

    [Fact]
    public void EditsAndArrangeGlideWhenTheCanvasAsksForMotion()
    {
        Assert.False(_canvas.Scene.IsMoving);
        _canvas.Arrange(LayoutShape.TopToBottom); // no Motion given (tests, Outline view): at once
        Assert.False(_canvas.Scene.IsMoving);

        _canvas.Motion = () => On;
        _canvas.Arrange(LayoutShape.LeftToRight);
        Assert.True(_canvas.Scene.IsMoving);
        _canvas.Scene.Settle();

        _canvas.Undo();
        Assert.True(_canvas.Scene.IsMoving);
        _canvas.Scene.Settle();

        _canvas.Motion = () => SceneMotion.For(animationsEnabled: false);
        _canvas.Redo();
        Assert.False(_canvas.Scene.IsMoving);
    }

    [Fact]
    public void FirstMeasuresJumpReMeasuresGlide()
    {
        _canvas.Motion = () => On;
        var first = _canvas.Nodes.First(n => n.CardId is null);
        _canvas.ReportSize(first.Id, 400, 56); // just opened: shown at once
        Assert.False(_canvas.Scene.IsMoving);
        _canvas.ReportSize(first.Id, 280, 56); // renamed: its neighbours glide
        Assert.True(_canvas.Scene.IsMoving);
    }
}
