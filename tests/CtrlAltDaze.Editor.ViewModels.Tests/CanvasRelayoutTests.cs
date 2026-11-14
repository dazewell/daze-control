using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>Second-round feedback: the canvas re-lays out after links and deletes and keeps no stale state.</summary>
public sealed class CanvasRelayoutTests : IDisposable
{
    private const string Yaml = """
        version: 1
        flows:
          f:
            name: Desk
            nodes: []
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;

    public CanvasRelayoutTests()
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

    private CanvasNodeViewModel Node(string id) => _canvas.Find(id) ?? throw new InvalidOperationException(id);

    private void AssertNoOverlap()
    {
        var top = _canvas.Nodes.Where(n => n.Kind != CanvasNodeKind.Member).ToList();
        foreach (var a in top)
        {
            foreach (var b in top.Where(b => b != a))
                Assert.False(a.Bounds.Intersects(b.Bounds) && a.Column == b.Column && Math.Abs(a.Y - b.Y) < 1, $"{a.Id} sits on {b.Id}");
        }
        foreach (var a in top)
        {
            foreach (var b in top.Where(b => string.CompareOrdinal(b.Id, a.Id) > 0))
                Assert.False(a.Bounds.Intersects(new Rect(b.X + 1, b.Y + 1, b.Width - 2, b.Height - 2)), $"{a.Id} overlaps {b.Id}");
        }
    }

    /// <summary>Each edge starts on its source's right middle and ends on its target's left middle.</summary>
    private void AssertEdgesOnPorts()
    {
        foreach (var edge in _canvas.Edges)
        {
            var from = Node(edge.From);
            var to = Node(edge.To);
            Assert.Equal(FlowCanvasViewModel.OutPort(from), edge.Curve.Start);
            Assert.Equal(FlowCanvasViewModel.InPort(to).Y, edge.Curve.End.Y, 3);
        }
    }

    [Fact]
    public void LinkingWithinAColumnMovesTheTargetToTheNextColumn()
    {
        // The user's steps: two pills in the first column, then link Restore → Show.
        var restore = _canvas.AddStep("display.layout")!;
        var show = _canvas.AddStep("notify")!;
        Assert.Equal(0, Node(restore).Column);
        Assert.Equal(0, Node(show).Column);

        Assert.Equal(ConnectRefusal.None, _canvas.Connect(restore, show));

        Assert.Equal(0, Node(restore).Column);
        Assert.Equal(1, Node(show).Column);
        AssertNoOverlap();
        AssertEdgesOnPorts();
        Assert.Single(_canvas.Edges);
        Assert.True(Node(show).X >= Node(restore).Bounds.Right);
    }

    [Fact]
    public void AfterASameColumnLinkOnlyRealLoopsAreRefused()
    {
        var restore = _canvas.AddStep("display.layout")!;
        var show = _canvas.AddStep("notify")!;
        _canvas.Connect(restore, show);
        var wait = _canvas.AddStep("wait")!; // a first step, column 0

        Assert.Equal(ConnectRefusal.Loop, _canvas.CanConnect(show, restore));
        Assert.Equal(ConnectRefusal.Exists, _canvas.CanConnect(restore, show));
        Assert.Equal(ConnectRefusal.None, _canvas.CanConnect(wait, show));
        Assert.Equal(ConnectRefusal.None, _canvas.CanConnect(show, wait));
        Assert.Equal(ConnectRefusal.None, _canvas.Connect(show, wait));
        Assert.Equal(2, Node(wait).Column);
        AssertNoOverlap();
        AssertEdgesOnPorts();

        // Moving a step to another column ("Run after") still works; only a real loop is refused.
        Assert.Equal(ConnectRefusal.Loop, _canvas.Drop(restore, wait, DropZone.RunAfter));
        var extra = _canvas.AddStep("wait")!;
        Assert.Equal(ConnectRefusal.None, _canvas.Drop(extra, wait, DropZone.RunAfter));
        Assert.Equal(3, Node(extra).Column);
        AssertNoOverlap();
        AssertEdgesOnPorts();
    }

    [Fact]
    public void UndoAfterASameColumnLinkPutsBothBack()
    {
        var restore = _canvas.AddStep("display.layout")!;
        var show = _canvas.AddStep("notify")!;
        _canvas.Connect(restore, show);
        _canvas.Undo();
        Assert.Equal(0, Node(show).Column);
        Assert.Empty(_canvas.Edges);
        AssertNoOverlap();
    }

    [Fact]
    public void DeletingOneOfTwoStackedCardsMovesTheEdgeOntoTheOther()
    {
        var first = _canvas.AddStep("display.layout")!;
        var a = _canvas.AddStep("notify", [first])!;
        var b = _canvas.AddStep("wait", [first])!;
        var c = _canvas.AddStep("notify", [first])!;
        var d = _canvas.AddStep("wait", [first])!;
        _canvas.SelectOnly([a, b]);
        Assert.Equal(ConnectRefusal.None, _canvas.GroupSelection());
        var card1 = _canvas.SelectedIds.Single();
        _canvas.SelectOnly([c, d]);
        Assert.Equal(ConnectRefusal.None, _canvas.GroupSelection());
        var card2 = _canvas.SelectedIds.Single();
        Assert.Equal(1, Node(card1).Column);
        Assert.Equal(1, Node(card2).Column);
        AssertEdgesOnPorts();

        _canvas.Select(card2);
        _canvas.DeleteSelection();

        Assert.Null(_canvas.Find(card2));
        Assert.Equal(card1, _canvas.Edges.Single().To);
        AssertEdgesOnPorts();
        AssertNoOverlap();
    }
}
