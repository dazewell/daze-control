using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>
/// The live drag (D-119 b, T21.4): every box and every line the preview shows for an offset is exactly what
/// <see cref="FlowCanvasViewModel.MoveBy"/> commits for it, so the drop changes nothing visible (D-120).
/// </summary>
public sealed class DragPreviewTests : IDisposable
{
    private const string Yaml = FlowCanvasViewModelTests.Yaml + """

          long:
            name: Long chain
            nodes:
              - { id: s1, step: wait, with: { duration: 1s } }
              - { id: s2, step: wait, with: { duration: 1s }, after: [s1] }
              - { id: s3, step: wait, with: { duration: 1s }, after: [s2] }
              - { id: s4, step: wait, with: { duration: 1s }, after: [s3] }
              - { id: s5, step: wait, with: { duration: 1s }, after: [s4] }
              - { id: s6, step: wait, with: { duration: 1s }, after: [s5] }
              - { id: s7, step: wait, with: { duration: 1s }, after: [s6] }
              - { id: s8, step: wait, with: { duration: 1s }, after: [s7] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly EditorSettingsStore _store;
    private FlowCanvasViewModel _canvas;

    public DragPreviewTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
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

    private void Open(string flowId)
    {
        _canvas.Dispose();
        _canvas = new FlowCanvasViewModel(_session, flowId, settings: _store);
    }

    public static TheoryData<LayoutShape, bool> Shapes()
    {
        var data = new TheoryData<LayoutShape, bool>();
        foreach (var shape in Enum.GetValues<LayoutShape>())
        {
            data.Add(shape, false);
            data.Add(shape, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void OneStepPreviewIsTheCommit(LayoutShape shape, bool placed)
    {
        Shape(shape, placed);
        AssertPreviewIsCommit(["vol"], 173, -61);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ACardPreviewIsTheCommit(LayoutShape shape, bool placed)
    {
        Shape(shape, placed);
        AssertPreviewIsCommit(["w1"], 90, 210); // a member's free move carries its card
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ASelectionPreviewIsTheCommit(LayoutShape shape, bool placed)
    {
        Shape(shape, placed);
        AssertPreviewIsCommit(["out", "bt", "note"], -37, 333);
    }

    [Fact]
    public void AFirstDragStraightensAnUnrelatedWrappedLineAtTheStartNotOnRelease()
    {
        Open("long");
        var wrapped = _canvas.Edges.Where(e => e.Curve.IsWrapped).Select(e => (e.From, e.To)).ToList();
        Assert.NotEmpty(wrapped); // the chain wraps after 5 steps (D-060)
        Assert.DoesNotContain(wrapped, w => w.From == "s8" || w.To == "s8");

        var preview = _canvas.BeginMove(["s8"])!;
        foreach (var (from, to) in wrapped)
        {
            Assert.DoesNotContain((from, to), preview.Touching); // unrelated: drawn once, from the frozen arrangement
            Assert.False(preview.StaticCurve(from, to)!.Value.IsWrapped);
        }
        AssertPreviewIsCommit(["s8"], 40, 120);
        Assert.DoesNotContain(_canvas.Edges, e => e.Curve.IsWrapped);
    }

    [Fact]
    public void TheFrameMovesTheCardsMembersAndOnlyTheTouchingLines()
    {
        var preview = _canvas.BeginMove(["both"])!;
        Assert.Equal(["both", "w1", "w2"], preview.Moved.Order());
        var frame = preview.At(new Point(16, 8));
        foreach (var id in preview.Moved)
            Assert.Equal(_canvas.Find(id)!.Bounds with { X = _canvas.Find(id)!.X + 16, Y = _canvas.Find(id)!.Y + 8 }, frame.Rects[id]);
        var touching = frame.Routes.Where(r => r.From == "both" || r.To == "both").ToList();
        Assert.Equal(frame.Routes.Count, frame.Curves.Count);
        Assert.Equal(preview.Frozen.Edges.Count, preview.StaticEdges.Count + touching.Count);
        // Any other line in the frame is a still one re-routed round the moved card (D-124).
        foreach (var r in frame.Routes.Except(touching))
            Assert.NotEqual(preview.StaticEdges.Single(e => (e.From, e.To) == (r.From, r.To)), r);
    }

    [Fact]
    public void AStillLineThatStartsDetouringMidDragRedrawsTheStaticSet()
    {
        _canvas.Arrange(LayoutShape.TopToBottom);
        var preview = _canvas.BeginMove(["w1"])!;
        var scene = _canvas.Scene;
        scene.BeginDrag(preview);

        // bt → vol runs in the gap left of the card (F-040: 14 DIP off it, not round everything), so it starts following
        // the card on the first frame: the live set grows and the static set is redrawn without it.
        Assert.True(scene.DragTo(preview.Offset(0, 8)));
        Assert.True(scene.LiveSetChanged);
        Assert.Equal([("bt", "vol")], scene.LiveCurves.Keys.Where(k => !preview.Touching.Contains(k)));

        // The card moves onto or off bt → vol's way: it re-routes (D-124). Where 90, 210 would crowd a step, it takes the
        // nearest spot that keeps the gaps (176, 296; UX §5.9).
        Assert.True(scene.DragTo(preview.Offset(90, 210)));
        Assert.Contains(("bt", "vol"), scene.LiveCurves.Keys);
        Assert.NotEqual(preview.StaticCurve("bt", "vol"), scene.LiveCurves[("bt", "vol")]);

        Assert.True(scene.DragTo(preview.Offset(176, 312))); // same lines follow: only the live set is drawn
        Assert.False(scene.LiveSetChanged);
        scene.EndDrag();
    }

    [Fact]
    public void NoPreviewWhenNothingCanMove()
    {
        Assert.Null(_canvas.BeginMove(["nope"]));
        _canvas.IsRunLocked = true;
        Assert.Null(_canvas.BeginMove(["vol"]));
    }

    private void Shape(LayoutShape shape, bool placed)
    {
        _canvas.Arrange(shape);
        if (placed)
            Assert.True(_canvas.MoveBy(["vol"], 240, 160));
        Assert.Equal(placed, _canvas.IsPlacedByHand);
    }

    private void AssertPreviewIsCommit(string[] ids, double dx, double dy)
    {
        var preview = _canvas.BeginMove(ids)!;
        var offset = preview.Offset(dx, dy);
        Assert.Equal(_canvas.SnappedOffset(ids, dx, dy), offset);
        var frame = preview.At(offset);
        var boxes = preview.Frozen.Boxes.ToDictionary(b => b.Id, b => frame.Rects.TryGetValue(b.Id, out var r) ? r : b.Bounds);
        var curves = preview.StaticEdges.ToDictionary(e => (e.From, e.To), e => EdgeCurve.For(e, preview.Lines));
        for (var i = 0; i < frame.Routes.Count; i++)
            curves[(frame.Routes[i].From, frame.Routes[i].To)] = frame.Curves[i]; // the frame's: touching or detouring (D-124)

        var shift = default(Point); // a move above or left of the flow moves its origin (F-030): everything by the same
        _canvas.OriginShifted += (_, s) => shift = s;
        Assert.True(_canvas.MoveBy(ids, dx, dy));

        Assert.Equal(boxes.Count, _canvas.Arrangement.Boxes.Count);
        foreach (var box in _canvas.Arrangement.Boxes)
            Near(boxes[box.Id], box.Bounds with { X = box.Bounds.X - shift.X, Y = box.Bounds.Y - shift.Y }, box.Id);
        Assert.Equal(curves.Count, _canvas.Edges.Count);
        foreach (var edge in _canvas.Edges)
            Near(Moved(curves[(edge.From, edge.To)], shift), edge.Curve, $"{edge.From}→{edge.To}");
    }

    private static EdgeCurve Moved(EdgeCurve c, Point by)
    {
        if (by == default)
            return c;
        Point M(Point p) => new(p.X + by.X, p.Y + by.Y);
        return c with
        {
            Start = M(c.Start), Control1 = M(c.Control1), Control2 = M(c.Control2), End = M(c.End), Tip = M(c.Tip),
            WrapLaneY = c.WrapLaneY + by.Y, BendX = c.BendX + by.X, BendY = c.BendY + by.Y,
            Elbow = c.Elbow is { } e ? M(e) : null,
        };
    }

    private static void Near(Rect want, Rect got, string what)
    {
        Assert.True(Close(want.X, got.X) && Close(want.Y, got.Y) && Close(want.Width, got.Width) && Close(want.Height, got.Height),
            $"{what}: preview {want}, commit {got}");
    }

    private static void Near(EdgeCurve want, EdgeCurve got, string what)
    {
        Assert.True(Close(want.Start, got.Start) && Close(want.Control1, got.Control1) && Close(want.Control2, got.Control2)
            && Close(want.End, got.End) && Close(want.Tip, got.Tip) && want.WrapLaneY == got.WrapLaneY
            && Close(want.BendX, got.BendX) && Close(want.BendY, got.BendY) && Close(want.Elbow, got.Elbow),
            $"{what}: preview {want}, commit {got}");
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static bool Close(double? a, double? b) => a is null ? b is null : b is not null && Close(a.Value, b.Value);

    private static bool Close(Point a, Point b) => Close(a.X, b.X) && Close(a.Y, b.Y);

    private static bool Close(Point? a, Point? b) => a is null ? b is null : b is not null && Close(a.Value, b.Value);
}
