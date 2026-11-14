using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>Hand placement and Arrange on the canvas (D-115): view state in editor.json, with undo.</summary>
public sealed class CanvasPlacementTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private EditorSettingsStore _store;
    private FlowCanvasViewModel _canvas;

    public CanvasPlacementTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(ConfigPath, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(ConfigPath, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        _store = new EditorSettingsStore(SettingsPath, _time);
        _canvas = new FlowCanvasViewModel(_session, "f", settings: _store);
    }

    private string ConfigPath => Path.Combine(_folder, "config.yaml");

    private string SettingsPath => Path.Combine(_folder, "editor.json");

    private CanvasNodeViewModel Node(string id) => _canvas.Find(id) ?? throw new InvalidOperationException(id);

    private Point At(string id) => new(Node(id).X, Node(id).Y);

    private Dictionary<string, Point> Everything() =>
        _canvas.Nodes.Where(n => n.CardId is null).ToDictionary(n => n.Id, n => new Point(n.X, n.Y));

    public void Dispose()
    {
        _canvas.Dispose();
        _store.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void ADropOnEmptyCanvasPlacesTheStepOnTheGridAndFreezesTheRest()
    {
        var before = Everything();
        var vol = At("vol");

        var offset = _canvas.SnappedOffset(["vol"], 301, 197);

        Assert.True(_canvas.MoveBy(["vol"], 301, 197));

        Assert.True(_canvas.IsPlacedByHand);
        Assert.Equal(new Point(vol.X + offset.X, vol.Y + offset.Y), At("vol"));
        // On the grid, or lined up with a step's column (UX §5.9), and clear of every other step.
        Assert.True(At("vol").X == FlowPlacement.Snap(vol.X + 301) || before.Any(kv => kv.Key != "vol" && kv.Value.X == At("vol").X));
        Assert.Equal(FlowPlacement.Snap(vol.Y + 197), At("vol").Y);
        foreach (var (id, at) in before.Where(kv => kv.Key != "vol"))
            Assert.Equal(at, At(id));
        Assert.Equal(before.Keys.Order(), _canvas.LayoutChoices.Placed!.Keys.Order());
        Assert.False(_canvas.HasChanges); // not a flow edit
    }

    [Fact]
    public void AMoveRaisesChangesOnlyOnTheMovedStep()
    {
        _canvas.MoveBy(["vol"], 160, 80); // the first move freezes the rest
        var raised = new List<string>();
        foreach (var node in _canvas.Nodes)
            node.PropertyChanged += (_, e) => raised.Add($"{node.Id}.{e.PropertyName}");

        _canvas.MoveBy(["vol"], 16, 8);

        // Each event is a full pill refresh in the view (T21.1: a drop used to re-raise Badges and AutomationName on every node).
        Assert.Equal(["vol.X", "vol.Y"], raised.Order());
    }

    [Theory]
    [InlineData(301, 197)]
    [InlineData(3, -3)]
    [InlineData(-5000, 40)]
    [InlineData(0, 0)]
    public void ThePreviewOffsetIsWhatTheDropCommits(double dx, double dy)
    {
        string[] ids = ["vol", "out", "w1"]; // a member counts as its card
        var before = ids.Select(At).ToList();
        var shift = default(Point);
        _canvas.OriginShifted += (_, s) => shift = s;

        var preview = _canvas.SnappedOffset(ids, dx, dy);
        _canvas.MoveBy(ids, dx, dy);

        // D-120: every box lands exactly where the preview drew it (less the origin shift of a drop above or left, F-030).
        foreach (var (id, was) in ids.Zip(before))
            Assert.Equal(new Point(was.X + preview.X + shift.X, was.Y + preview.Y + shift.Y), At(id));
    }

    [Fact]
    public void ThePreviewOffsetOfNothingIsZero() =>
        Assert.Equal(default, _canvas.SnappedOffset(["nope"], 40, 40));

    private static readonly Size Pill = new(232, 56);

    private static Rect Box(double x, double y) => new(x, y, Pill.Width, Pill.Height);

    [Theory]
    [InlineData(96, 96, 13, 6, 16, 8)] // the lead snaps: 109 → 112, 102 → 104 (8-DIP grid)
    [InlineData(101, 99, 0, 0, 3, -3)] // a lead off the grid snaps even when the pointer hasn't moved
    [InlineData(100, 100, -500, -500, -500, -500)] // nothing stops it above or left of the flow (F-030)
    public void MoveSnapSnapsTheLeadAndGoesAnywhereFree(double x, double y, double dx, double dy, double wantX, double wantY)
    {
        var offset = MoveSnap.Offset([Box(x, y)], [], dx, dy, 64, 16);

        Assert.Equal(new Point(wantX, wantY), offset);
    }

    [Fact]
    public void MoveSnapLinesTheLeadUpWithANearbyColumn()
    {
        // A still step at x 300 (off the grid); the lead dropped 20 DIP right of that column, far below it, joins it.
        var offset = MoveSnap.Offset([Box(1000, 1000)], [Box(301, 0)], -680, 0, 64, 16);

        Assert.Equal(301, 1000 + offset.X);
    }

    [Fact]
    public void MoveSnapNeverOverlapsAndKeepsTheGaps()
    {
        // The finding (F-030): a drop 8 DIP over a neighbour in its column. It goes under it, a row gap away.
        Rect[] still = [Box(48, 48), Box(48 + 232 + 64, 48)];
        var lead = Box(600, 400);
        var offset = MoveSnap.Offset([lead], still, 48 - 600, 48 + 56 - 8 - 400, 64, 16);

        var landed = lead with { X = lead.X + offset.X, Y = lead.Y + offset.Y };
        Assert.True(MoveSnap.Clear([lead], still, offset.X, offset.Y, 64, 16));
        Assert.Equal(48, landed.X); // still in the column
        Assert.Equal(48 + 56 + 16, landed.Y); // one row gap under its neighbour
    }

    [Fact]
    public void MoveSnapKeepsAGroupRigidAndClearOfEveryStillStep()
    {
        Rect[] movers = [Box(1000, 48), Box(1000, 48 + 56 + 16)];
        Rect[] still = [Box(48, 48), Box(48, 48 + 56 + 16), Box(48, 48 + 2 * (56 + 16))];
        var offset = MoveSnap.Offset(movers, still, -940, 8, 64, 16);

        Assert.True(MoveSnap.Clear(movers, still, offset.X, offset.Y, 64, 16));
        Assert.True(Math.Abs(offset.X - -940) + Math.Abs(offset.Y - 8) < 400); // the nearest free spot, not far away
    }

    [Theory]
    [InlineData(48 + 232 + 63, 48, false)] // beside it, one DIP inside the column gap
    [InlineData(48 + 232 + 64, 48, true)] // exactly the column gap
    [InlineData(48, 48 + 56 + 15, false)] // under it, one DIP inside the row gap
    [InlineData(48, 48 + 56 + 16, true)] // exactly the row gap
    [InlineData(48 + 232 + 10, 48 + 56 + 16, true)] // diagonal: a row gap below is enough
    public void ClearMeansTheColumnGapBesideOrTheRowGapAboveAndBelow(double x, double y, bool clear) =>
        Assert.Equal(clear, MoveSnap.Clear([Box(x, y)], [Box(48, 48)], 0, 0, 64, 16));

    [Fact]
    public void AMoveAboveTheFlowShiftsTheOriginAndUndoPutsItBack()
    {
        var before = Everything();
        var vol = At("vol");
        var shifts = new List<Point>();
        _canvas.OriginShifted += (_, s) => shifts.Add(s);

        Assert.True(_canvas.MoveBy(["vol"], 0, -vol.Y - 400)); // 400 DIP above the top of the flow (F-030)

        var shift = Assert.Single(shifts);
        Assert.True(shift.Y > 0); // everything moved down, so the moved step is at the margin
        Assert.Equal(_canvas.LayoutOptions.Padding, _canvas.Nodes.Where(n => n.CardId is null).Min(n => n.Y));
        foreach (var (id, at) in before.Where(kv => kv.Key != "vol"))
            Assert.Equal(new Point(at.X + shift.X, at.Y + shift.Y), At(id)); // the rest keep their places on screen

        _canvas.Undo();

        Assert.Equal(new Point(-shift.X, -shift.Y), shifts[^1]); // the camera pans back by it
        Assert.Equal(before, Everything());
    }

    [Theory]
    [InlineData(LayoutShape.LeftToRight)]
    [InlineData(LayoutShape.TopToBottom)]
    [InlineData(LayoutShape.Tree)]
    [InlineData(LayoutShape.Circle)]
    public void AfterEachArrangeTheFitShowsEveryStep(LayoutShape shape)
    {
        var raised = 0;
        _canvas.Rearranged += (_, _) => raised++;
        _canvas.MoveBy(["vol"], 400, 400); // placed far off by hand first (F-015, F-040)

        _canvas.Arrange(shape);
        var camera = _canvas.FitView(1000, 700);

        Assert.Equal(1, raised); // the canvas fits on it (FlowCanvas.OnRearranged)
        Assert.All(_canvas.Nodes, n =>
        {
            var (from, to) = (camera.ToScreen(new Point(n.X, n.Y)), camera.ToScreen(new Point(n.X + n.Width, n.Y + n.Height)));
            Assert.True(from.X >= 0 && from.Y >= 0 && to.X <= 1000 && to.Y <= 700, $"{n.Id} at {from}–{to}");
        });
    }

    [Fact]
    public void ArrangeAndResetLayoutAskTheCanvasToFit()
    {
        var raised = 0;
        _canvas.Rearranged += (_, _) => raised++;

        _canvas.MoveBy(["vol"], 400, 400);
        _canvas.Arrange(LayoutShape.LeftToRight);
        _canvas.Arrange(LayoutShape.LeftToRight); // already so: still brings it into view (F-015)
        _canvas.ResetLayout();

        Assert.Equal(3, raised);
    }

    [Fact]
    public void TheSelectionMovesTogether()
    {
        var offset = new Point(At("out").X - At("vol").X, At("out").Y - At("vol").Y);

        _canvas.MoveBy(["vol", "out"], 400, 400);

        Assert.Equal(offset, new Point(At("out").X - At("vol").X, At("out").Y - At("vol").Y));
    }

    [Fact]
    public void AMemberMovesItsCard()
    {
        var card = At("both");
        var member = At("w1");

        _canvas.MoveBy(["w1"], 200, 160);

        Assert.Equal(At("both").X - card.X, At("w1").X - member.X);
        Assert.Equal(At("both").Y - card.Y, At("w1").Y - member.Y);
    }

    [Fact]
    public void NothingGoesAboveOrLeftOfThePadding()
    {
        _canvas.MoveBy(["vol"], -5000, -5000);

        Assert.Equal(new Point(LayoutOptions.Default.Padding, LayoutOptions.Default.Padding), At("vol"));
    }

    [Fact]
    public void UndoAndRedoAMove()
    {
        var auto = Everything();
        _canvas.MoveBy(["bt"], 0, 400);
        var moved = At("bt");

        Assert.True(_canvas.CanUndo);
        _canvas.Undo();
        Assert.False(_canvas.IsPlacedByHand);
        Assert.Equal(auto, Everything());

        _canvas.Redo();
        Assert.Equal(moved, At("bt"));
    }

    [Fact]
    public void MovesAndFlowEditsShareOneUndoHistory()
    {
        _canvas.MoveBy(["bt"], 0, 400);
        var moved = At("bt");
        var id = _canvas.AddStep("wait", ["note"]);
        Assert.NotNull(_canvas.Find(id!));
        Assert.True(Node(id!).X > Node("note").X); // beside the step it runs after

        _canvas.Undo(); // the new step goes
        Assert.Null(_canvas.Find(id!));
        Assert.Equal(moved, At("bt")); // still placed

        _canvas.Undo(); // then the move
        Assert.False(_canvas.IsPlacedByHand);
    }

    [Fact]
    public void ArrangeLaysEverythingOutAfreshAndUndoBringsTheHandPlacementBack()
    {
        _canvas.MoveBy(["vol"], 300, 300);
        var placed = Everything();

        _canvas.Arrange(LayoutShape.Tree);

        Assert.False(_canvas.IsPlacedByHand);
        Assert.Equal(LayoutShape.Tree, _canvas.LayoutOptions.Shape);
        Assert.True(Node("out").Y > Node("bt").Y); // under the step it follows
        Assert.Equal(PortSide.Bottom, _canvas.OutSide);
        Assert.Equal(Node("bt").Bounds.Bottom, _canvas.OutPortOf(Node("bt")).Y);

        _canvas.Undo();
        Assert.Equal(placed, Everything());
        Assert.Equal(LayoutShape.LeftToRight, _canvas.LayoutOptions.Shape);
    }

    [Fact]
    public void ArrangeLeftToRightStoresNothing()
    {
        _canvas.Arrange(LayoutShape.Circle);
        _canvas.Arrange(LayoutShape.LeftToRight);

        Assert.True(_canvas.LayoutChoices.IsDefault);
        Assert.Empty(_store.Settings.Flows);
    }

    [Fact]
    public void ResetLayoutLetsGoOfHandPlacementAndCanBeUndone()
    {
        _canvas.MoveBy(["vol"], 300, 300);

        _canvas.ResetLayout();
        Assert.True(_canvas.LayoutChoices.IsDefault);

        _canvas.Undo();
        Assert.True(_canvas.IsPlacedByHand);
    }

    [Fact]
    public void PlacementAndShapeSurviveARestart()
    {
        _canvas.Arrange(LayoutShape.TopToBottom);
        _canvas.MoveBy(["vol"], 120, 64);
        var placed = Everything();

        _canvas.Dispose();
        _store.Dispose();
        _store = new EditorSettingsStore(SettingsPath, _time);
        _canvas = new FlowCanvasViewModel(_session, "f", settings: _store);

        Assert.Equal(LayoutShape.TopToBottom, _canvas.LayoutOptions.Shape);
        Assert.Equal(placed, Everything());
        Assert.Contains("\"placed\"", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ArrowKeysFollowWhatIsDrawnInOtherShapes()
    {
        // Top to bottom: out and vol (both after bt) share a column, now drawn side by side in one row.
        _canvas.Arrange(LayoutShape.TopToBottom);
        _canvas.Select("bt");

        _canvas.MoveFocus(FocusMove.Down);
        var first = _canvas.FocusedId;
        Assert.True(first is "out" or "vol", first);

        _canvas.MoveFocus(Node("out").X < Node("vol").X == (first == "out") ? FocusMove.Right : FocusMove.Left);
        Assert.Equal(first == "out" ? "vol" : "out", _canvas.FocusedId);
    }

    [Fact]
    public void LinesFollowTheSidesThatFaceEachOther()
    {
        // vol (after bt) dragged far left of and below bt: its line no longer leaves bt's right side.
        _canvas.MoveBy(["bt"], 800, 0);
        var edge = _canvas.Edges.Single(e => e.From == "bt" && e.To == "vol");

        Assert.NotEqual(FlowCanvasViewModel.OutPort(Node("bt")), edge.Curve.Start);
    }
}
