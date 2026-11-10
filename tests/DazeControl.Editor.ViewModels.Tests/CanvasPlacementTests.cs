using DazeControl.Core.Layout;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Hand placement and Arrange on the canvas (D-115): view state in editor.json, with undo.</summary>
public sealed class CanvasPlacementTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
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

        Assert.True(_canvas.MoveBy(["vol"], 301, 197));

        Assert.True(_canvas.IsPlacedByHand);
        Assert.Equal(new Point(FlowPlacement.Snap(vol.X + 301), FlowPlacement.Snap(vol.Y + 197)), At("vol"));
        foreach (var (id, at) in before.Where(kv => kv.Key != "vol"))
            Assert.Equal(at, At(id));
        Assert.Equal(before.Keys.Order(), _canvas.LayoutChoices.Placed!.Keys.Order());
        Assert.False(_canvas.HasChanges); // not a flow edit
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
    public void LinesFollowTheSidesThatFaceEachOther()
    {
        // vol (after bt) dragged far left of and below bt: its line no longer leaves bt's right side.
        _canvas.MoveBy(["bt"], 800, 0);
        var edge = _canvas.Edges.Single(e => e.From == "bt" && e.To == "vol");

        Assert.NotEqual(FlowCanvasViewModel.OutPort(Node("bt")), edge.Curve.Start);
    }
}
