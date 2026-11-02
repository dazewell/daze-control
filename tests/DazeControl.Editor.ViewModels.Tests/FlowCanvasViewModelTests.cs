using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class FlowCanvasViewModelTests : IDisposable
{
    internal const string Yaml = """
        version: 1
        library:
          audio: { technics: { match: "Technics EAH-AZ80" } }
          bluetooth: { technics: { address: "B8:20:8E:ED:48:FC", name: "Technics" } }
        flows:
          f:
            name: Connect Technics
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }
              - { id: out, step: audio.setDefault, with: { device: technics }, after: [bt], onFailure: ignore }
              - { id: vol, step: audio.setVolume, with: { percent: "30" }, after: [bt], retry: { count: 3, delay: 1s }, timeout: 5s }
              - { id: note, step: notify, with: { text: Done }, after: [{ id: out, when: failure }] }
              - { id: both, group: [w1, w2] }
              - { id: w1, step: wait, with: { duration: 1s } }
              - { id: w2, step: wait, with: { duration: 2s } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;

    public FlowCanvasViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
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

    private ValueList<string> Order() => [.. _canvas.Flow!.Nodes.Select(n => n.Id)];

    [Fact]
    public void PillsShowTitleSummaryGlyphAndBadges()
    {
        var bt = Node("bt");
        Assert.Equal("Connect", bt.Title);
        Assert.Equal("Technics", bt.Summary);
        Assert.Equal("", bt.Glyph);
        Assert.Equal("Technics EAH-AZ80", Node("out").Summary);
        Assert.True(Node("out").MayFail);
        Assert.Equal(["May fail"], Node("out").Badges.Select(b => b.Text));
        Assert.Equal(["×3", "5s"], Node("vol").Badges.Select(b => b.Text));
        Assert.Equal("30", Node("vol").Summary);
    }

    [Fact]
    public void CardsHoldMembersAndDrawFirst()
    {
        var card = Node("both");
        Assert.Equal(CanvasNodeKind.Card, card.Kind);
        Assert.Equal("Run together", card.Title);
        Assert.Equal("2 steps", card.Count);
        Assert.Equal(CanvasNodeKind.Member, Node("w1").Kind);
        Assert.Equal("both", Node("w1").CardId);
        Assert.True(_canvas.Nodes.IndexOf(card) < _canvas.Nodes.IndexOf(Node("w1")));
        Assert.True(card.Bounds.Contains(new Point(Node("w2").X + 1, Node("w2").Y + 1)));
    }

    [Fact]
    public void EdgesCarryTheirStyle()
    {
        Assert.Equal(EdgeStyle.IntoMayFail, _canvas.Edges.Single(e => e.To == "out").Style);
        Assert.Equal(EdgeStyle.Normal, _canvas.Edges.Single(e => e.To == "vol").Style);
        var fallback = _canvas.Edges.Single(e => e.To == "note");
        Assert.Equal(EdgeStyle.IfFails, fallback.Style);
        Assert.Equal("If it fails", fallback.Chip);
    }

    [Fact]
    public void AutomationNameSaysWhatItWaitsFor()
    {
        Assert.Equal("Set default device, Technics EAH-AZ80. Audio. Waits for Connect. May fail.", Node("out").AutomationName);
        Assert.Equal("Connect, Technics. Bluetooth.", Node("bt").AutomationName);
    }

    [Fact]
    public void EditsReLayOutInPlace()
    {
        var bt = Node("bt");
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes.Select(n => n.Id == "out" ? n with { After = [] } : n)] });

        Assert.Same(bt, Node("bt"));
        Assert.Equal(0, Node("out").Column);
        Assert.True(_session.HasUnsavedChanges);
    }

    [Fact]
    public void MeasuredSizesWidenThePill()
    {
        _canvas.ReportSize("bt", 300, 80);

        Assert.Equal(300, Node("bt").Width);
        Assert.Equal(80, Node("bt").Height);
        Assert.Equal(Node("bt").Bounds.Right + 64, Node("out").X);
    }

    [Fact]
    public void FitNeverZoomsPastActualSizeOrBelowTheMinimum()
    {
        Assert.Equal(1, _canvas.FitView(10_000, 10_000).Zoom);
        Assert.Equal(FlowCanvasViewModel.MinZoom, _canvas.FitView(10, 10).Zoom);
        var (zoom, x, _) = _canvas.FitView(_canvas.ExtentWidth * 0.75, _canvas.ExtentHeight * 2);
        Assert.Equal(0.75, zoom, 3);
        Assert.Equal(0, x, 3);
    }

    [Fact]
    public void SemanticZoomHidesDetailsBelowSixtyPercent()
    {
        _canvas.Zoom = 0.59;
        Assert.False(_canvas.ShowDetails);
        _canvas.Zoom = 0.6;
        Assert.True(_canvas.ShowDetails);
        Assert.Equal("60 %", _canvas.ZoomText);
    }

    [Fact]
    public void ClickSelectsOneCtrlClickToggles()
    {
        _canvas.Select("bt");
        _canvas.Toggle("out");
        Assert.True(Node("bt").IsSelected && Node("out").IsSelected);
        Assert.True(_canvas.Edges.Single(e => e.To == "out").IsSelected);

        _canvas.Toggle("bt");
        Assert.False(Node("bt").IsSelected);

        _canvas.Select("vol");
        Assert.Equal(["vol"], _canvas.SelectedIds);
    }

    [Fact]
    public void MarqueeSelectsWhatItTouches()
    {
        var bt = Node("bt").Bounds;
        _canvas.SelectIn(new Rect(bt.X - 10, bt.Y - 10, 20, 20), additive: false);
        Assert.Equal(["bt"], _canvas.SelectedIds);

        var outBox = Node("out").Bounds;
        _canvas.SelectIn(new Rect(outBox.X, outBox.Y, 5, 5), additive: true);
        Assert.Equal(["bt", "out"], _canvas.SelectedIds.Order());
    }

    [Fact]
    public void SelectAllAndClear()
    {
        _canvas.SelectAll();
        Assert.Equal(_canvas.Nodes.Count, _canvas.SelectedIds.Count);
        _canvas.ClearSelection();
        Assert.Empty(_canvas.SelectedIds);
        Assert.DoesNotContain(_canvas.Nodes, n => n.IsSelected);
    }

    [Fact]
    public void SelectionSurvivesEditsButNotDeletion()
    {
        _canvas.Select("vol");
        _canvas.Edit(f => f with { Name = "Renamed" });
        Assert.True(Node("vol").IsSelected);
        Assert.Equal("Renamed", _canvas.FlowName);

        _canvas.Edit(f => f with { Nodes = [.. f.Nodes.Where(n => n.Id != "vol")] });
        Assert.Empty(_canvas.SelectedIds);
    }

    [Fact]
    public void HitTestPrefersAMemberOverItsCard()
    {
        var w1 = Node("w1").Bounds;
        Assert.Equal("w1", _canvas.HitTest(new Point(w1.X + 2, w1.Y + 2))?.Id);
        var card = Node("both").Bounds;
        Assert.Equal("both", _canvas.HitTest(new Point(card.X + 2, card.Y + 2))?.Id);
        Assert.Null(_canvas.HitTest(new Point(1, 1)));
    }

    [Fact]
    public void EdgeHitTestUsesABand()
    {
        var edge = _canvas.Edges.Single(e => e.To == "note");
        var mid = edge.Curve.Midpoint;

        Assert.Same(edge, _canvas.HitTestEdge(new Point(mid.X, mid.Y + 10)));
        Assert.Null(_canvas.HitTestEdge(new Point(mid.X, mid.Y + 30)));
    }

    [Fact]
    public void ArrowKeysRoveBetweenColumnsAndRows()
    {
        _canvas.EnsureFocus();
        Assert.Equal("bt", _canvas.FocusedId);
        _canvas.MoveFocus(FocusMove.Down);
        Assert.Equal("both", _canvas.FocusedId);
        _canvas.MoveFocus(FocusMove.Up);
        _canvas.MoveFocus(FocusMove.Right);
        Assert.Equal(1, Node(_canvas.FocusedId!).Column);
        Assert.True(Node(_canvas.FocusedId!).IsFocused);
        Assert.True(Node(_canvas.FocusedId!).IsSelected);
        _canvas.MoveFocus(FocusMove.Left);
        Assert.Equal("bt", _canvas.FocusedId);
    }

    [Fact]
    public void AltDownReordersWithinTheColumn()
    {
        Assert.Equal(["bt", "both"], _canvas.Arrangement.Columns[0]);

        _canvas.MoveInColumn("bt", +1);

        Assert.Equal(["both", "bt"], _canvas.Arrangement.Columns[0]);
        Assert.Equal(["both", "out", "vol", "note", "bt", "w1", "w2"], Order());
    }

    [Fact]
    public void DraggingAMemberReordersTheCard()
    {
        var w1 = Node("w1");
        var below = Node("w2").Bounds.Bottom + 1;
        Assert.Equal(1, _canvas.RowAt("w1", below));

        _canvas.MoveToRow("w1", _canvas.RowAt("w1", below));

        Assert.Equal(["w2", "w1"], ((GroupNode)_canvas.Flow!.FindNode("both")!).Members);
        Assert.True(Node("w2").Y < w1.Y);
    }

    [Fact]
    public void DroppingInPlaceChangesNothing()
    {
        _canvas.MoveToRow("out", _canvas.RowAt("out", Node("out").Bounds.CenterY));

        Assert.False(_session.HasUnsavedChanges);
    }

    [Fact]
    public void DraggingFromAnOutputPortConnects()
    {
        var port = FlowCanvasViewModel.OutPort(Node("vol"));
        Assert.Equal("vol", _canvas.OutPortAt(new Point(port.X + 5, port.Y - 5))?.Id);
        Assert.Null(_canvas.OutPortAt(FlowCanvasViewModel.OutPort(Node("w1")))); // members: ports on the card

        Assert.Equal(ConnectRefusal.None, _canvas.Connect("vol", "w2"));

        Assert.Contains(new Edge("vol"), _canvas.Flow!.FindNode("both")!.After);
        Assert.Contains(_canvas.Edges, e => e.From == "vol" && e.To == "both");
    }

    [Fact]
    public void ALoopIsRefusedWithTheTeachingText()
    {
        var refusal = _canvas.Connect("note", "bt");

        Assert.Equal(ConnectRefusal.Loop, refusal);
        Assert.StartsWith("That would make a loop.", FlowCanvasViewModel.Explain(refusal));
        Assert.False(_session.HasUnsavedChanges);
        Assert.Equal(ConnectRefusal.SameCard, _canvas.CanConnect("w1", "w2"));
    }

    [Fact]
    public void EdgeMenuChangesWhenAndRemoves()
    {
        var edge = _canvas.Edges.Single(e => e.To == "vol");

        _canvas.SetWhen(edge, EdgeWhen.Always);
        Assert.Equal(EdgeStyle.Always, _canvas.Edges.Single(e => e.To == "vol").Style);

        _canvas.Disconnect(_canvas.Edges.Single(e => e.To == "vol"));
        Assert.DoesNotContain(_canvas.Edges, e => e.To == "vol");
        Assert.Equal(0, Node("vol").Column);
    }

    [Fact]
    public void CtrlGGroupsTheSelectionAndSelectsTheCard()
    {
        _canvas.Select("out");
        _canvas.Toggle("vol");

        Assert.Equal(ConnectRefusal.None, _canvas.GroupSelection());

        var card = Node(_canvas.SelectedIds.Single());
        Assert.Equal(CanvasNodeKind.Card, card.Kind);
        Assert.Equal(card.Id, Node("out").CardId);
        Assert.Equal(card.Id, Node("vol").CardId);
        Assert.Contains(_canvas.Edges, e => e.From == "bt" && e.To == card.Id);
    }

    [Fact]
    public void CtrlGRefusesStepsThatWaitForEachOther()
    {
        _canvas.Select("bt");
        _canvas.Toggle("out");

        Assert.Equal(ConnectRefusal.SameCard, _canvas.GroupSelection());
        Assert.False(_session.HasUnsavedChanges);
    }

    [Fact]
    public void CtrlShiftGUngroupsAndSelectsTheMembers()
    {
        _canvas.Select("both");

        _canvas.UngroupSelection();

        Assert.Null(_canvas.Find("both"));
        Assert.Equal(["w1", "w2"], _canvas.SelectedIds.Order());
        Assert.Equal(CanvasNodeKind.Step, Node("w1").Kind);
    }

    [Fact]
    public void DropZonesFollowThePointer()
    {
        var target = Node("vol");
        Assert.Equal(DropZone.RunAfter, FlowCanvasViewModel.ZoneAt(target, new Point(target.Bounds.Right - 2, target.Bounds.CenterY)));
        Assert.Equal(DropZone.RunTogether, FlowCanvasViewModel.ZoneAt(target, new Point(target.X + 10, target.Bounds.CenterY)));
        Assert.Equal(DropZone.None, FlowCanvasViewModel.ZoneAt(target, new Point(target.X + 10, target.Y + 2)));
        Assert.Equal(DropZone.RunTogether, FlowCanvasViewModel.ZoneAt(Node("both"), new Point(Node("both").X + 10, Node("both").Y + 2)));
    }

    [Fact]
    public void DroppingAPillOnACardJoinsIt()
    {
        Assert.Equal(ConnectRefusal.None, _canvas.Drop("note", "both", DropZone.RunTogether));

        Assert.Equal("both", Node("note").CardId);
        Assert.Equal(["w1", "w2", "note"], ((GroupNode)_canvas.Flow!.FindNode("both")!).Members);
    }

    [Fact]
    public void DroppingAMemberOutDissolvesATwoStepCard()
    {
        _canvas.TakeOutOfCard("w1");

        Assert.Null(_canvas.Find("both"));
        Assert.Null(Node("w2").CardId);
    }

    [Fact]
    public void DeletedFlowShowsAsMissing()
    {
        _session.Update(c => c with { Flows = [] });

        Assert.True(_canvas.IsMissing);
        Assert.True(_canvas.IsEmpty);
    }
}
