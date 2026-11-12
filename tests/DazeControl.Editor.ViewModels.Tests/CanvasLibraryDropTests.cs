using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>A step dragged from the library lands where the pointer drops it (D-120, T21.5).</summary>
public sealed class CanvasLibraryDropTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly EditorSettingsStore _store;
    private readonly FlowCanvasViewModel _canvas;

    public CanvasLibraryDropTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
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

    private CanvasNodeViewModel Node(string id) => _canvas.Find(id) ?? throw new InvalidOperationException(id);

    private Point Mid(string id) => new(Node(id).X + Node(id).Width / 2, Node(id).Bounds.CenterY);

    private Dictionary<string, Point> Everything() =>
        _canvas.Nodes.Where(n => n.CardId is null).ToDictionary(n => n.Id, n => new Point(n.X, n.Y));

    /// <summary>A point on empty canvas, right of and below every step.</summary>
    private Point Empty => new(_canvas.ExtentWidth + 300, _canvas.ExtentHeight + 200);

    private static void AssertCentredOn(Point p, CanvasNodeViewModel node, double tolerance = FlowPlacement.Grid / 2)
    {
        Assert.InRange((node.X + node.Width / 2), p.X - tolerance, p.X + tolerance);
        Assert.InRange(node.Bounds.CenterY, p.Y - tolerance, p.Y + tolerance);
        Assert.Equal(0, node.X % FlowPlacement.Grid);
        Assert.Equal(0, node.Y % FlowPlacement.Grid);
    }

    [Fact]
    public void OnEmptyCanvasTheStepLandsCentredUnderThePointerAndTheRestFreezes()
    {
        var before = Everything();
        var p = new Point(Empty.X + 3, Empty.Y - 5); // off the grid

        var (id, refusal) = _canvas.AddStepAt("wait", p);

        Assert.Equal(ConnectRefusal.None, refusal);
        var node = Node(id!);
        AssertCentredOn(p, node);
        Assert.True(_canvas.IsPlacedByHand); // an auto-laid-out flow becomes placed by hand (D-115)
        foreach (var (other, at) in before)
            Assert.Equal(at, new Point(Node(other).X, Node(other).Y));
        Assert.Equal([id!], _canvas.SelectedIds);
        Assert.True(_canvas.HasChanges);
        Assert.Empty(_canvas.Flow!.FindNode(id!)!.After); // runs on its own until connected
    }

    [Fact]
    public void TheLandingOutlineIsExactlyWhereTheStepGoes()
    {
        var p = Empty;
        var outline = _canvas.LandingAt(p, FlowCanvasViewModel.DefaultPillSize);

        var (id, _) = _canvas.AddStepAt("wait", p);

        Assert.Equal(outline, Node(id!).Bounds);
    }

    [Fact]
    public void AboveAndLeftOfTheFlowItStillLandsAtThePointerAndTheFlowShiftsWithIt()
    {
        var before = Everything();
        var p = new Point(-500, -300);
        var landing = _canvas.LandingAt(p, FlowCanvasViewModel.DefaultPillSize);
        Point? shift = null;
        _canvas.OriginShifted += (_, s) => shift = s;

        var (id, _) = _canvas.AddStepAt("wait", p);

        // The layout keeps its margin by moving everything; the canvas pans back by the shift, so on screen the new step
        // is where the outline was and nothing else moved.
        var node = Node(id!);
        Assert.Equal(new Point(LayoutOptions.Default.Padding, LayoutOptions.Default.Padding), new Point(node.X, node.Y));
        Assert.NotNull(shift);
        Assert.Equal(new Point(landing.X, landing.Y), new Point(node.X - shift.Value.X, node.Y - shift.Value.Y));
        foreach (var (other, at) in before)
            Assert.Equal(at, new Point(Node(other).X - shift.Value.X, Node(other).Y - shift.Value.Y));
    }

    [Fact]
    public void ADropOnEmptyCanvasDoesNotShiftTheFlow()
    {
        var shifted = false;
        _canvas.OriginShifted += (_, _) => shifted = true;
        _canvas.AddStepAt("wait", Empty);
        Assert.False(shifted);
    }

    [Theory]
    [InlineData("vol", 0.5, 0.05)] // a pill's thin top band (no zone)
    [InlineData("vol", 0.5, 0.95)] // its bottom band
    [InlineData("vol", 0.5, -0.3)] // just above it, half covering it
    [InlineData("vol", 1.15, 0.5)] // just right of it, where the new pill would cover it
    public void ItNeverLandsOnTopOfAnotherStep(string near, double fx, double fy)
    {
        var n = Node(near);
        var p = new Point(n.X + n.Width * fx, n.Y + n.Height * fy);
        var (target, zone) = (n.Bounds.Contains(p) ? n.Id : null, n.Bounds.Contains(p) ? FlowCanvasViewModel.ZoneAt(n, p) : DropZone.None);
        if (zone != DropZone.None)
            return; // a zone drop is an edit, not a placement
        var outline = _canvas.LandingAt(p, FlowCanvasViewModel.DefaultPillSize);
        var others = Everything().Keys.ToList();
        var shift = new Point(0, 0); // landing above the flow moves its origin (the canvas pans back)
        _canvas.OriginShifted += (_, s) => shift = s;

        var (id, _) = _canvas.AddStepAt("wait", p, target, zone);

        var mine = Node(id!).Bounds;
        Assert.Equal(new Point(outline.X, outline.Y), new Point(mine.X - shift.X, mine.Y - shift.Y));
        var room = new Rect(mine.X, mine.Y, FlowCanvasViewModel.MaxPillWidth, mine.Height); // as wide as it may grow
        foreach (var other in others)
            Assert.False(room.Intersects(Node(other).Bounds), $"covers {other}");
    }

    [Fact]
    public void OneUndoTakesAwayTheStepAndItsPlacementAndRedoBringsBoth()
    {
        var auto = Everything();
        var (id, _) = _canvas.AddStepAt("wait", Empty);
        var placed = Everything();

        _canvas.Undo();

        Assert.Null(_canvas.Find(id!));
        Assert.Null(_canvas.Flow!.FindNode(id!));
        Assert.False(_canvas.IsPlacedByHand);
        Assert.Equal(auto, Everything());
        Assert.False(_canvas.CanUndo);
        Assert.False(_canvas.HasChanges);

        _canvas.Redo();

        Assert.NotNull(_canvas.Flow!.FindNode(id!));
        Assert.Equal(placed, Everything());
        Assert.True(_canvas.IsPlacedByHand);
    }

    [Fact]
    public void ASecondDropOnAPlacedFlowKeepsEveryStepWhereItIs()
    {
        _canvas.MoveBy(["vol"], 160, 80);
        var (first, _) = _canvas.AddStepAt("wait", Empty);
        var before = Everything();
        var p = new Point(Empty.X + 400, Empty.Y);

        var (second, _) = _canvas.AddStepAt("notify", p);

        AssertCentredOn(p, Node(second!));
        foreach (var (id, at) in before)
            Assert.Equal(at, new Point(Node(id).X, Node(id).Y));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void OnAStepsRightThirdItRunsAfterIt()
    {
        var (id, refusal) = _canvas.AddStepAt("wait", Mid("note"), "note", DropZone.RunAfter);

        Assert.Equal(ConnectRefusal.None, refusal);
        Assert.Contains(_canvas.Flow!.FindNode(id!)!.After, e => e.From == "note");
        Assert.False(_canvas.IsPlacedByHand); // a zone drop is a flow edit: the layout places it
        Assert.Equal([id!], _canvas.SelectedIds);
    }

    [Fact]
    public void OnAStepsLeftThirdItRunsBeforeIt()
    {
        var (id, _) = _canvas.AddStepAt("wait", Mid("bt"), "bt", DropZone.RunBefore);

        Assert.Contains(_canvas.Flow!.FindNode("bt")!.After, e => e.From == id);
    }

    [Fact]
    public void OnAStepsBodyItRunsTogetherWithIt()
    {
        var (id, _) = _canvas.AddStepAt("wait", Mid("vol"), "vol", DropZone.RunTogether);

        Assert.Equal(Node("vol").CardId, Node(id!).CardId);
        Assert.NotNull(Node(id!).CardId);
    }

    [Fact]
    public void AZoneDropIsOneUndoStep()
    {
        var before = _canvas.Flow;
        _canvas.AddStepAt("wait", Mid("vol"), "vol", DropZone.RunTogether);

        _canvas.Undo();

        Assert.Equal(before, _canvas.Flow);
        Assert.False(_canvas.CanUndo);
    }

    [Fact]
    public void NoZoneOnAStepPlacesItAtThePointer()
    {
        var p = Empty;

        var (id, _) = _canvas.AddStepAt("wait", p, "vol", DropZone.None); // a pill's thin top/bottom band, as for a step drag

        AssertCentredOn(p, Node(id!));
        Assert.True(_canvas.IsPlacedByHand);
    }

    /// <summary>
    /// The canvas reads the drop in Host (world) coordinates, which XAML works out through the camera transform
    /// (D-119 e). Whatever the zoom and pan, the step's centre comes back to the screen point it was dropped on, within
    /// half a grid cell of content (snapping).
    /// </summary>
    [Theory]
    [InlineData(0.5, 0, 0, 300, 200)]
    [InlineData(0.5, 640, 120, 900, 500)]
    [InlineData(1.0, 0, 0, 700, 450)]
    [InlineData(1.0, 1200, 900, 37, 611)]
    [InlineData(2.0, 0, 0, 1100, 650)]
    [InlineData(2.0, 2400, 1700, 513, 77)]
    public void ADropAtAnyZoomAndScrollLandsUnderThePointer(double zoom, double scrollX, double scrollY, double sx, double sy)
    {
        // Pan past the steps so the pointer is over empty canvas, then by the case's own amount.
        var camera = new Camera(-_canvas.ExtentWidth * zoom - scrollX, -_canvas.ExtentHeight * zoom - scrollY, zoom);
        var host = camera.ToWorld(new Point(sx, sy));
        Assert.Null(_canvas.HitTest(host));

        var (id, _) = _canvas.AddStepAt("wait", host);

        var node = Node(id!);
        AssertCentredOn(host, node);
        var (screenX, screenY) = camera.ToScreen(new Point(node.X + node.Width / 2, node.Bounds.CenterY));
        Assert.InRange(screenX, sx - FlowPlacement.Grid / 2 * zoom, sx + FlowPlacement.Grid / 2 * zoom);
        Assert.InRange(screenY, sy - FlowPlacement.Grid / 2 * zoom, sy + FlowPlacement.Grid / 2 * zoom);
    }
}
