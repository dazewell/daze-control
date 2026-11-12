using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>
/// The retained scene (D-119 a, T21.6): the grid-indexed hit tests return exactly what a scan of every box, port and
/// line returns (the code they replaced), and <see cref="CanvasScene.Apply"/> reports only what changed.
/// </summary>
public sealed class CanvasSceneTests : IDisposable
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
              - { id: s9, step: notify, with: { text: Done }, after: [s1, s4] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly EditorSettingsStore _store;
    private FlowCanvasViewModel _canvas;

    public CanvasSceneTests()
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

    // ---- Brute force: the scans the index replaced ----

    private static SceneItem? ScanHit(CanvasScene scene, Point p) =>
        scene.Items.Where(i => i.Bounds.Contains(p)).OrderBy(i => i.Kind == SceneKind.Card ? 1 : 0).FirstOrDefault();

    private static SceneEdge? ScanEdge(CanvasScene scene, Point p, double band) =>
        scene.Edges.Select(e => (Edge: e, Distance: e.Curve.DistanceTo(p))).Where(x => x.Distance <= band)
            .OrderBy(x => x.Distance).Select(x => x.Edge).FirstOrDefault();

    private static SceneItem? ScanPort(CanvasScene scene, Point p, double radius, bool output) =>
        scene.Items.Where(i => i.Kind != SceneKind.Member)
            .Select(i => (Item: i, Distance: CanvasScene.Distance(output ? scene.OutPort(i) : scene.InPort(i), p)))
            .Where(x => x.Distance <= radius).OrderBy(x => x.Distance).Select(x => x.Item).FirstOrDefault();

    private static List<SceneItem> ScanIn(CanvasScene scene, Rect area) => [.. scene.Items.Where(i => i.Bounds.Intersects(area))];

    /// <summary>Random points over the scene plus points on and near every line and port, so hits are tested, not just misses.</summary>
    private static IEnumerable<Point> Probes(CanvasScene scene, Random random, int count)
    {
        var (w, h) = (Math.Max(scene.Extent.Width, 100), Math.Max(scene.Extent.Height, 100));
        for (var i = 0; i < count; i++)
            yield return new Point(random.NextDouble() * (w + 200) - 100, random.NextDouble() * (h + 200) - 100);
        Point Jitter(Point p, double by) => new(p.X + (random.NextDouble() * 2 - 1) * by, p.Y + (random.NextDouble() * 2 - 1) * by);
        foreach (var edge in scene.Edges)
        {
            for (var t = 0.0; t <= 1; t += 0.05)
                yield return Jitter(edge.Curve.At(t), 24);
            yield return Jitter(edge.Curve.Tip, 8);
        }
        foreach (var item in scene.Items)
        {
            yield return Jitter(scene.OutPort(item), 16);
            yield return Jitter(scene.InPort(item), 16);
            yield return new Point(item.Bounds.Right, item.Bounds.Bottom); // the inclusive corner
            yield return new Point(item.Bounds.X, item.Bounds.Y);
        }
        // Points right on cell lines (the grid's own edge case).
        for (var x = -256.0; x <= w + 256; x += SpatialGrid.DefaultCell)
            for (var y = -256.0; y <= h + 256; y += SpatialGrid.DefaultCell / 2)
                yield return new Point(x, y);
    }

    private static void AssertSameAsScan(CanvasScene scene, int seed)
    {
        var random = new Random(seed);
        var hits = 0;
        foreach (var p in Probes(scene, random, 1500))
        {
            Assert.Equal(ScanHit(scene, p)?.Id, scene.HitTest(p)?.Id);
            foreach (var band in new[] { 12.0, 20.0 })
            {
                var expected = ScanEdge(scene, p, band);
                Assert.Equal(expected?.Key, scene.HitTestEdge(p, band)?.Key);
                hits += expected is null ? 0 : 1;
            }
            Assert.Equal(ScanPort(scene, p, FlowCanvasViewModel.PortRadius, true)?.Id, scene.OutPortAt(p, FlowCanvasViewModel.PortRadius)?.Id);
            Assert.Equal(ScanPort(scene, p, FlowCanvasViewModel.PortRadius, false)?.Id, scene.InPortAt(p, FlowCanvasViewModel.PortRadius)?.Id);
        }
        for (var i = 0; i < 200; i++)
        {
            var a = new Point(random.NextDouble() * scene.Extent.Width, random.NextDouble() * scene.Extent.Height);
            var b = new Point(a.X + random.NextDouble() * 600 - 300, a.Y + random.NextDouble() * 600 - 300);
            var area = Rect.FromCorners(a, b);
            Assert.Equal(ScanIn(scene, area).Select(x => x.Id), scene.ItemsIn(area).Select(x => x.Id));
        }
        Assert.True(scene.Edges.Count == 0 || hits > 0, "the probes never hit a line");
    }

    /// <summary>
    /// A random scene: overlapping steps, cards with members (some members poke out of their card), lines between top-level
    /// boxes routed the way the layout routes them, some with wrap or detour lanes.
    /// </summary>
    private static (FlowArrangement Arrangement, SceneSetup Setup) RandomScene(Random random, int steps)
    {
        var boxes = new List<NodeBox>();
        var cards = new HashSet<string>(StringComparer.Ordinal);
        var shape = (LayoutShape)random.Next(Enum.GetValues<LayoutShape>().Length);
        for (var i = 0; i < steps; i++)
        {
            var at = new Rect(random.Next(-40, 2400), random.Next(-40, 1600), random.Next(200, 330), random.Next(56, 90));
            if (random.NextDouble() < 0.15)
            {
                var id = $"card{i}";
                var members = random.Next(0, 4);
                var card = at with { Height = 48 + members * 64 };
                boxes.Add(new NodeBox(id, card, i, 0, null));
                cards.Add(id);
                for (var m = 0; m < members; m++)
                    boxes.Add(new NodeBox($"{id}.m{m}", new Rect(card.X + 8, card.Y + 40 + m * 64, random.Next(180, card.Width > 200 ? (int)card.Width + 40 : 220), 56), i, m, id));
            }
            else
            {
                boxes.Add(new NodeBox($"s{i}", at, i, 0, null));
            }
        }
        var top = boxes.Where(b => b.Card is null).ToList();
        var edges = new List<EdgeRoute>();
        for (var i = 0; i < top.Count * 2 && top.Count > 1; i++)
        {
            var (a, b) = (top[random.Next(top.Count)], top[random.Next(top.Count)]);
            if (a.Id == b.Id || edges.Any(e => e.From == a.Id && e.To == b.Id))
                continue;
            var (start, end) = FlowPlacement.Sides(a.Bounds, b.Bounds, shape);
            double? lane = random.NextDouble() < 0.2 ? Math.Max(a.Bounds.Bottom, b.Bounds.Bottom) + random.Next(8, 40) : null;
            var when = (EdgeWhen)random.Next(Enum.GetValues<EdgeWhen>().Length);
            edges.Add(new EdgeRoute(a.Id, b.Id, when, FlowPlacement.Port(a.Bounds, start), FlowPlacement.Port(b.Bounds, end), lane, start, end));
        }
        var extent = boxes.Count == 0 ? new Size(96, 96) : new Size(boxes.Max(b => b.Bounds.Right) + 48, boxes.Max(b => b.Bounds.Bottom) + 48);
        // Shuffled: the scene orders top-level boxes before members itself.
        var shuffled = boxes.OrderBy(_ => random.Next()).ToList();
        var mayFail = top.Where(_ => random.NextDouble() < 0.3).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var lines = random.NextDouble() < 0.5 ? LayoutLines.Curved : LayoutLines.RightAngles;
        return (new FlowArrangement(shuffled, edges, [], extent), new SceneSetup(lines, shape, cards, mayFail));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 6)]
    [InlineData(4, 25)]
    [InlineData(5, 60)]
    [InlineData(6, 120)]
    public void IndexedHitTestsMatchAScanOfEverything(int seed, int steps)
    {
        var random = new Random(seed);
        var scene = new CanvasScene();
        for (var round = 0; round < 3; round++) // and again after re-applies: the index is rebuilt each time
        {
            var (arrangement, setup) = RandomScene(random, steps);
            scene.Apply(arrangement, setup);
            Assert.Equal(arrangement.Boxes.Count, scene.Items.Count);
            AssertSameAsScan(scene, seed * 10 + round);
        }
    }

    [Fact]
    public void ItemsAreTopLevelFirstAndFoundById()
    {
        var (arrangement, setup) = RandomScene(new Random(7), 40);
        var scene = new CanvasScene();
        scene.Apply(arrangement, setup);
        var firstMember = scene.Items.ToList().FindIndex(i => i.Kind == SceneKind.Member);
        Assert.True(firstMember < 0 || scene.Items.Skip(firstMember).All(i => i.Kind == SceneKind.Member));
        foreach (var box in arrangement.Boxes)
            Assert.Equal(box, scene.Find(box.Id)?.Box);
        Assert.Null(scene.Find("nope"));
        foreach (var route in arrangement.Edges)
            Assert.Equal(route, scene.FindEdge(route.From, route.To)?.Route);
    }

    // ---- The view model's hit tests go through the scene ----

    public static TheoryData<string, LayoutShape, LayoutLines> Flows()
    {
        var data = new TheoryData<string, LayoutShape, LayoutLines>();
        foreach (var flow in new[] { "f", "long" })
            foreach (var shape in Enum.GetValues<LayoutShape>())
                foreach (var lines in Enum.GetValues<LayoutLines>())
                    data.Add(flow, shape, lines);
        return data;
    }

    [Theory]
    [MemberData(nameof(Flows))]
    public void CanvasHitTestsMatchTheOldScans(string flowId, LayoutShape shape, LayoutLines lines)
    {
        Open(flowId);
        _canvas.Arrange(shape);
        _canvas.SetLayout(lines: lines);
        var random = new Random(flowId.Length * 31 + (int)shape * 7 + (int)lines);
        foreach (var p in Probes(_canvas.Scene, random, 800))
        {
            // The scans FlowCanvasViewModel used before T21.6, over its own Nodes and Edges.
            var node = _canvas.Nodes.Where(n => n.Bounds.Contains(p)).OrderBy(n => n.Kind == CanvasNodeKind.Card ? 1 : 0).FirstOrDefault();
            Assert.Same(node, _canvas.HitTest(p));
            var edge = _canvas.Edges.Select(e => (Edge: e, Distance: e.Curve.DistanceTo(p))).Where(x => x.Distance <= 12)
                .OrderBy(x => x.Distance).Select(x => x.Edge).FirstOrDefault();
            Assert.Same(edge, _canvas.HitTestEdge(p));
            var port = _canvas.Nodes.Where(n => n.Kind != CanvasNodeKind.Member)
                .Select(n => (Node: n, Distance: CanvasScene.Distance(_canvas.OutPortOf(n), p)))
                .Where(x => x.Distance <= FlowCanvasViewModel.PortRadius).OrderBy(x => x.Distance).Select(x => x.Node).FirstOrDefault();
            Assert.Same(port, _canvas.OutPortAt(p));
            var into = node ?? _canvas.Nodes.Where(n => n.Kind != CanvasNodeKind.Member)
                .Select(n => (Node: n, Distance: CanvasScene.Distance(_canvas.InPortOf(n), p)))
                .Where(x => x.Distance <= FlowCanvasViewModel.PortRadius).OrderBy(x => x.Distance).Select(x => x.Node).FirstOrDefault();
            Assert.Same(into?.CardId is { } card ? _canvas.Find(card) : into, _canvas.ConnectTargetAt(p));
        }
    }

    [Fact]
    public void NodesAndEdgesFollowTheScene()
    {
        Assert.Equal(_canvas.Scene.Items.Select(i => i.Id), _canvas.Nodes.Select(n => n.Id));
        Assert.Equal(_canvas.Scene.Edges.Select(e => e.Key), _canvas.Edges.Select(e => (e.From, e.To)));
        foreach (var node in _canvas.Nodes)
        {
            Assert.Same(node, _canvas.Find(node.Id));
            Assert.Equal(node.Bounds, _canvas.Scene.Find(node.Id)!.Bounds);
        }
        Assert.Equal(SceneKind.Card, _canvas.Scene.Find("both")!.Kind);
        Assert.Equal(SceneKind.Member, _canvas.Scene.Find("w1")!.Kind);
        Assert.Equal(EdgeStyle.IntoMayFail, _canvas.Scene.FindEdge("bt", "out")!.Style);
        Assert.Equal(EdgeStyle.IfFails, _canvas.Scene.FindEdge("out", "note")!.Style);
    }

    // ---- Apply's diff ----

    private static NodeBox Box(string id, double x, double y, double w = 232, double h = 56, string? card = null) => new(id, new Rect(x, y, w, h), 0, 0, card);

    private static EdgeRoute Route(NodeBox a, NodeBox b, EdgeWhen when = EdgeWhen.Success) =>
        new(a.Id, b.Id, when, FlowPlacement.Port(a.Bounds, PortSide.Right), FlowPlacement.Port(b.Bounds, PortSide.Left));

    private static FlowArrangement Arrangement(IReadOnlyList<NodeBox> boxes, IReadOnlyList<EdgeRoute> edges) => new(boxes, edges, [], new Size(1000, 600));

    [Fact]
    public void ApplyingTheSameArrangementChangesNothing()
    {
        var (a, b) = (Box("a", 48, 48), Box("b", 400, 48));
        var scene = new CanvasScene();
        var first = scene.Apply(Arrangement([a, b], [Route(a, b)]));
        Assert.Equal(["a", "b"], first.Added);
        Assert.Equal([("a", "b")], first.EdgesAdded);
        var again = scene.Apply(Arrangement([a, b], [Route(a, b)]));
        Assert.True(again.IsEmpty);
        Assert.False(again.EdgesChanged);
    }

    [Fact]
    public void TheDiffNamesOnlyWhatChanged()
    {
        NodeBox a = Box("a", 48, 48), b = Box("b", 400, 48), c = Box("c", 400, 200), d = Box("d", 800, 48);
        var scene = new CanvasScene();
        scene.Apply(Arrangement([a, b, c, d], [Route(a, b), Route(a, c), Route(b, d)]),
            new SceneSetup(Content: id => id == "c" ? "old" : "same"));

        // b moves (its lines re-route), c gets new content, d grows, a stays; e is new, nothing removed yet.
        NodeBox b2 = Box("b", 400, 96), d2 = Box("d", 800, 48, 300), e = Box("e", 48, 300);
        var diff = scene.Apply(Arrangement([a, b2, c, d2, e], [Route(a, b2), Route(a, c), Route(b2, d2), Route(a, e)]),
            new SceneSetup(Content: id => id == "c" ? "new" : "same"));
        Assert.Equal(["e"], diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Equal(["b"], diff.Moved);
        Assert.Equal(["d"], diff.Resized);
        Assert.Equal(["c"], diff.ContentChanged);
        Assert.Equal([("a", "e")], diff.EdgesAdded);
        Assert.Equal([("a", "b"), ("b", "d")], diff.RoutesChanged); // a→c didn't move: not redrawn for nothing
        Assert.Empty(diff.StylesChanged);

        // c goes with its line; a→b turns into "if it fails".
        var gone = scene.Apply(Arrangement([a, b2, d2, e], [Route(a, b2, EdgeWhen.Failure), Route(b2, d2), Route(a, e)]),
            new SceneSetup(Content: _ => "same"));
        Assert.Equal(["c"], gone.Removed);
        Assert.Equal([("a", "c")], gone.EdgesRemoved);
        Assert.Equal([("a", "b")], gone.StylesChanged);
        Assert.Empty(gone.RoutesChanged);
        Assert.Null(scene.Find("c"));
        Assert.Null(scene.HitTest(new Point(c.Bounds.X + 10, c.Bounds.Y + 10)));
    }

    [Fact]
    public void BecomingACardOrAMemberIsAContentChange()
    {
        var (a, b) = (Box("a", 48, 48), Box("b", 400, 48));
        var scene = new CanvasScene();
        scene.Apply(Arrangement([a, b], []));
        var diff = scene.Apply(Arrangement([a, b with { Card = "a" }], []), new SceneSetup(Cards: new HashSet<string> { "a" }));
        Assert.Equal(["a", "b"], diff.ContentChanged);
        Assert.Equal(SceneKind.Card, scene.Find("a")!.Kind);
        Assert.Equal(SceneKind.Member, scene.Find("b")!.Kind);
        Assert.False(scene.Find("b")!.HasPorts);
    }

    [Fact]
    public void TheVerticalShapesPortTopAndBottom()
    {
        var a = Box("a", 48, 48);
        var scene = new CanvasScene();
        scene.Apply(Arrangement([a], []), new SceneSetup(Shape: LayoutShape.TopToBottom));
        Assert.Equal(FlowPlacement.Port(a.Bounds, PortSide.Bottom), scene.OutPort(scene.Find("a")!));
        Assert.Equal(FlowPlacement.Port(a.Bounds, PortSide.Top), scene.InPort(scene.Find("a")!));
        Assert.Same(scene.Find("a"), scene.OutPortAt(FlowPlacement.Port(a.Bounds, PortSide.Bottom), 1));
        Assert.Null(scene.OutPortAt(FlowPlacement.Port(a.Bounds, PortSide.Right), 1));
    }

    // ---- Through the canvas: a move touches only what it moved ----

    [Fact]
    public void AMoveReportsOnlyTheMovedBoxAndItsLines()
    {
        _canvas.MoveBy(["note"], 0, 0); // nothing moves: no commit
        Assert.True(_canvas.MoveBy(["vol"], 0, 400)); // the first move freezes the rest (D-115): only vol moves
        var diff = _canvas.LastDiff;
        Assert.Equal(["vol"], diff.Moved);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Resized);
        Assert.Empty(diff.ContentChanged);
        Assert.All(diff.RoutesChanged, k => Assert.True(k.From == "vol" || k.To == "vol", $"{k} doesn't touch vol"));
        Assert.Contains(("bt", "vol"), diff.RoutesChanged);
    }

    [Fact]
    public void LinesKeepTheirViewModelSoHoverAndSelectionStay()
    {
        Assert.True(_canvas.MoveBy(["note"], 0, 300)); // freeze first: the next move changes nothing else
        var edges = _canvas.Edges.ToDictionary(e => (e.From, e.To));
        var hovered = edges[("out", "note")];
        _canvas.HoveredEdge = hovered;
        var version = _canvas.EdgeVersion;
        Assert.True(_canvas.MoveBy(["note"], 0, 80));
        Assert.Same(hovered, _canvas.HoveredEdge);
        Assert.All(_canvas.Edges, e => Assert.Same(edges[(e.From, e.To)], e));
        Assert.Equal(_canvas.Scene.FindEdge("out", "note")!.Curve, hovered.Curve); // re-routed in place
        Assert.True(_canvas.EdgeVersion > version);
    }

    [Fact]
    public void EdgeVersionMovesOnlyWhenALineLooksDifferent()
    {
        var version = _canvas.EdgeVersion;
        _canvas.Select("w1"); // a member alone lights no line
        Assert.Equal(version, _canvas.EdgeVersion);
        _canvas.SelectOnly(["bt", "out"]);
        Assert.True(_canvas.EdgeVersion > version);
        version = _canvas.EdgeVersion;
        _canvas.SelectOnly(["bt", "out"]);
        Assert.Equal(version, _canvas.EdgeVersion);
        _canvas.HoveredEdge = _canvas.Edges[0];
        Assert.True(_canvas.EdgeVersion > version);
        version = _canvas.EdgeVersion;
        _canvas.HoveredEdge = _canvas.Edges[0];
        Assert.Equal(version, _canvas.EdgeVersion);
    }

    // ---- Transient layer ----

    [Fact]
    public void TheDragLivesInTheSceneUntilItEnds()
    {
        var scene = _canvas.Scene;
        Assert.False(scene.IsDragging);
        var preview = _canvas.BeginMove(["out"]);
        Assert.NotNull(preview);
        Assert.Same(preview, scene.Drag);
        var offset = preview.Offset(37, 51);
        Assert.True(scene.DragTo(offset));
        Assert.False(scene.DragTo(offset)); // same snapped offset: nothing to redraw
        var frame = scene.Frame!;
        Assert.Equal(frame.Rects["out"], scene.ShownBounds("out"));
        Assert.Equal(scene.Find("bt")!.Bounds, scene.ShownBounds("bt"));
        Assert.Contains(("bt", "out"), scene.LiveCurves.Keys);
        Assert.Equal(frame.Curves[frame.Routes.ToList().FindIndex(r => r.From == "bt" && r.To == "out")], scene.ShownCurve("bt", "out"));
        Assert.Equal(preview.StaticCurve("bt", "vol"), scene.ShownCurve("bt", "vol"));

        _canvas.EndMove();
        Assert.False(scene.IsDragging);
        Assert.Null(scene.Frame);
        Assert.Empty(scene.LiveCurves);
        Assert.Equal(scene.FindEdge("bt", "vol")!.Curve, scene.ShownCurve("bt", "vol"));
        Assert.Equal(scene.Find("out")!.Bounds, scene.ShownBounds("out"));
    }

    private void Open(string flowId)
    {
        _canvas.Dispose();
        _canvas = new FlowCanvasViewModel(_session, flowId, settings: _store);
    }
}
