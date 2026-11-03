using DazeControl.Core.Layout;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Edges are clickable, hoverable, marquee-selectable and deletable (UX §5.8 #2).</summary>
public sealed class CanvasEdgeSelectionTests : IDisposable
{
    private const string Yaml = """
        version: 1
        flows:
          f:
            name: Desk
            nodes:
              - id: a
                step: wait
              - id: b
                step: wait
                after: [a]
              - id: c
                step: wait
                after: [b]
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;

    public CanvasEdgeSelectionTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private CanvasEdgeViewModel Edge(string from, string to) => _canvas.Edges.Single(e => e.From == from && e.To == to);

    [Fact]
    public void AClickNearTheCurveFindsTheEdgeWithinItsBand()
    {
        var ab = Edge("a", "b");
        var mid = ab.Curve.Midpoint;

        Assert.Same(ab, _canvas.HitTestEdge(mid));
        Assert.Same(ab, _canvas.HitTestEdge(new Point(mid.X, mid.Y + 11)));
        Assert.Null(_canvas.HitTestEdge(new Point(mid.X, mid.Y + 16)));
        Assert.Same(ab, _canvas.HitTestEdge(new Point(mid.X, mid.Y + 16), band: 20));
        Assert.Null(_canvas.HitTestEdge(new Point(mid.X, mid.Y + 30), band: 20));
    }

    [Fact]
    public void SelectingAnEdgeSelectsOnlyIt()
    {
        _canvas.Select("a");

        _canvas.SelectEdge(Edge("a", "b"));

        Assert.Empty(_canvas.SelectedIds);
        Assert.True(Edge("a", "b").IsSelected);
        Assert.False(Edge("b", "c").IsSelected);
        Assert.Equal([("a", "b")], _canvas.SelectedEdges.Select(e => (e.From, e.To)));
    }

    [Fact]
    public void CtrlClickAddsAndRemovesEdges()
    {
        _canvas.SelectEdge(Edge("a", "b"));
        _canvas.SelectEdge(Edge("b", "c"), additive: true);
        Assert.Equal(2, _canvas.SelectedEdges.Count);

        _canvas.SelectEdge(Edge("a", "b"), additive: true);
        Assert.Equal([("b", "c")], _canvas.SelectedEdges.Select(e => (e.From, e.To)));
    }

    [Fact]
    public void DeleteRemovesTheSelectedEdgeOnlyAndUndoesInOneStep()
    {
        _canvas.SelectEdge(Edge("a", "b"));

        _canvas.DeleteSelection();

        Assert.Equal(3, _canvas.Nodes.Count);
        Assert.Equal([("b", "c")], _canvas.Edges.Select(e => (e.From, e.To)));
        Assert.Empty(_canvas.SelectedEdges);
        _canvas.Undo();
        Assert.Equal(2, _canvas.Edges.Count);
    }

    [Fact]
    public void DeletingAStepAndAnEdgeIntoItReconnectsAsForTheStepAlone()
    {
        _canvas.Select("b");
        _canvas.SelectEdge(Edge("a", "b"), additive: true);

        _canvas.DeleteSelection();

        Assert.Null(_canvas.Find("b"));
        Assert.Equal([("a", "c")], _canvas.Edges.Select(e => (e.From, e.To)));
    }

    [Fact]
    public void TheMarqueeTakesEdgesWithBothEndsInside()
    {
        var a = _canvas.Find("a")!;
        var b = _canvas.Find("b")!;
        // From a's middle to b's middle: both ends of a→b, no end of b→c.
        var area = Rect.FromCorners(new Point(a.X + a.Width / 2, a.Y - 10), new Point(b.X + b.Width / 2, b.Bounds.Bottom + 10));

        _canvas.SelectIn(area, additive: false);

        Assert.Equal([("a", "b")], _canvas.SelectedEdges.Select(e => (e.From, e.To)));
        Assert.False(Edge("b", "c").IsSelected);
    }

    [Fact]
    public void ClickingANodeOrEmptySpaceDropsTheEdgeSelection()
    {
        _canvas.SelectEdge(Edge("a", "b"));
        _canvas.Select("c");
        Assert.Empty(_canvas.SelectedEdges);

        _canvas.SelectEdge(Edge("a", "b"));
        _canvas.ClearSelection();
        Assert.False(Edge("a", "b").IsSelected);
    }

    [Fact]
    public void AnEdgeStaysSelectedAcrossARelayoutAndGoesWhenRemoved()
    {
        _canvas.SelectEdge(Edge("b", "c"));
        _canvas.SetWhen(Edge("b", "c"), Core.Model.EdgeWhen.Failure);
        Assert.True(Edge("b", "c").IsSelected);

        _canvas.Disconnect(Edge("b", "c"));
        Assert.Empty(_canvas.SelectedEdges);
    }

    [Fact]
    public void HoverMarksOneEdge()
    {
        _canvas.HoveredEdge = Edge("a", "b");
        Assert.True(Edge("a", "b").IsHovered);

        _canvas.HoveredEdge = Edge("b", "c");
        Assert.False(Edge("a", "b").IsHovered);
        Assert.Same(Edge("b", "c"), _canvas.HoveredEdge);

        _canvas.HoveredEdge = null;
        Assert.Null(_canvas.HoveredEdge);
    }

    [Fact]
    public void DeletingEveryMemberWithTheEdgeIntoTheCardStillReconnects()
    {
        // a → card(m1, m2) → z; select m1, m2 and the edge a → card: the card goes, and z waits for a.
        _canvas.Edit(f => f with
        {
            Nodes =
            [
                new Core.Model.StepNode("a", "wait"),
                new Core.Model.GroupNode("card", ["m1", "m2"]) { After = [new Core.Model.Edge("a")] },
                new Core.Model.StepNode("m1", "wait"),
                new Core.Model.StepNode("m2", "wait"),
                new Core.Model.StepNode("z", "wait") { After = [new Core.Model.Edge("card")] },
            ],
        });
        _canvas.SelectOnly(["m1", "m2"]);
        _canvas.SelectEdge(Edge("a", "card"), additive: true);

        _canvas.DeleteSelection();

        Assert.Null(_canvas.Find("card"));
        Assert.Equal([("a", "z")], _canvas.Edges.Select(e => (e.From, e.To)));
    }
}
