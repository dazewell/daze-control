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
        Assert.Equal((FlowCanvasViewModel.MinZoom, 0d, 0d), _canvas.FitView(10, 10)); // too big: starts at the top left
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

    private static readonly string[] MemberFirstOrder = ["bt", "out", "both", "w1", "w2", "vol", "note"];

    private static readonly string[] CrossedOrder = ["bt", "vol", "out", "note", "both", "w1", "w2"];

    [Fact]
    public void GroupingWithAnotherCardsMemberSelectsTheNewCard()
    {
        // w1 (in "both") comes first in `nodes`; Group ignores it, so the new card for vol + note is the one to select.
        _canvas.Edit(f => f with { Nodes = [.. MemberFirstOrder.Select(id => f.FindNode(id)!)] });
        _canvas.Select("w1");
        _canvas.Toggle("note");
        _canvas.Toggle("vol");

        Assert.Equal(ConnectRefusal.None, _canvas.GroupSelection());

        Assert.Equal("together", Assert.Single(_canvas.SelectedIds));
        Assert.Equal(["vol", "note"], ((GroupNode)_canvas.Flow!.FindNode("together")!).Members);
    }

    [Fact]
    public void AConnectDragEndsOnTheOverhangingHalfOfAnInputDot()
    {
        var port = FlowCanvasViewModel.InPort(Node("note"));

        Assert.Equal("note", _canvas.ConnectTargetAt(new Point(port.X - 3, port.Y))?.Id);
        Assert.Equal("both", _canvas.ConnectTargetAt(new Point(Node("w1").X + 5, Node("w1").Bounds.CenterY))?.Id);
        Assert.Null(_canvas.ConnectTargetAt(new Point(port.X - 30, port.Y)));
    }

    [Fact]
    public void TidyUpUncrossesColumnsAndSaves()
    {
        Assert.False(_canvas.TidyUp());

        // vol also waits for the card and sits above out: bt→out and both→vol cross.
        _canvas.Edit(f => f with
        {
            Nodes = [.. CrossedOrder.Select(id =>
                f.FindNode(id) is StepNode { Id: "vol" } vol ? vol with { After = [.. vol.After, new Edge("both")] } : f.FindNode(id)!)],
        });
        Assert.Equal(1, FlowTidy.Crossings(_canvas.Flow!));

        Assert.True(_canvas.TidyUp());

        Assert.Equal(["bt", "out", "vol", "note", "both", "w1", "w2"], Order());
        Assert.True(Node("out").Y < Node("vol").Y);
        Assert.Equal("vol", _canvas.HitTest(new Point(Node("vol").Bounds.X + 5, Node("vol").Bounds.CenterY))?.Id);
        Assert.True(_session.HasUnsavedChanges);
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
        // note runs only if out fails: out can't join a card (any member's failure would count).
        _canvas.Select("out");
        _canvas.Toggle("vol");
        Assert.Equal(ConnectRefusal.FailureFallback, _canvas.GroupSelection());

        _canvas.Select("vol");
        _canvas.Toggle("both");
        Assert.Equal(ConnectRefusal.None, _canvas.GroupSelection());

        var card = Node(_canvas.SelectedIds.Single());
        Assert.Equal("both", card.Id);
        Assert.Equal(CanvasNodeKind.Card, card.Kind);
        Assert.Equal(card.Id, Node("vol").CardId);
        Assert.Contains(_canvas.Edges, e => e.From == "bt" && e.To == card.Id);
    }

    /// <summary>The draft's errors, as the engine would see it once saved.</summary>
    private IEnumerable<string> DraftErrors() =>
        DazeControl.Core.Validation.ConfigValidator.Validate(_session.Current with { Flows = [_canvas.Flow!] })
            .Where(p => p.Severity == DazeControl.Core.Validation.Severity.Error).Select(p => $"{p.Path}: {p.Message}");

    private GroupNode Card(string memberId) =>
        Assert.IsType<GroupNode>(_canvas.Flow!.FindNode(Node(memberId).CardId ?? throw new InvalidOperationException(memberId)));

    [Fact]
    public void CtrlGOnLinkedStepsDropsTheLinkInOneUndoStep()
    {
        var original = _canvas.Flow;
        _canvas.Select("bt");
        _canvas.Toggle("vol");

        Assert.Equal(ConnectRefusal.None, _canvas.GroupSelection());

        var card = Card("vol");
        Assert.Equal(["bt", "vol"], card.Members);
        Assert.Empty(card.After);
        Assert.Contains(_canvas.Edges, e => e.From == card.Id && e.To == "out");
        Assert.Null(_canvas.TakeEditNote());
        Assert.Empty(DraftErrors());
        _canvas.Undo();
        Assert.Equal(original, _canvas.Flow);
    }

    [Fact]
    public void DroppingAStepOntoTheStepItWaitsForMakesACard()
    {
        Assert.Equal(ConnectRefusal.None, _canvas.Drop("vol", "bt", DropZone.RunTogether));

        Assert.Equal(["bt", "vol"], Card("vol").Members);
        Assert.Equal(CanvasNodeKind.Card, Node(Card("vol").Id).Kind);
        Assert.Empty(DraftErrors());
        Assert.True(_canvas.CanUndo);
    }

    [Fact]
    public void DroppingAStepOntoAStepThatWaitsForItMakesACard()
    {
        Assert.Equal(ConnectRefusal.None, _canvas.Drop("bt", "vol", DropZone.RunTogether));

        Assert.Equal(["vol", "bt"], Card("bt").Members);
        Assert.Empty(Card("bt").After);
        Assert.Empty(DraftErrors());
    }

    [Fact]
    public void DroppingTheEndOfAChainOntoItsStartSaysTheFallbackIsGone()
    {
        // bt → out →(if it fails) note; note dropped onto bt: [bt note] → out, and the fallback is gone.
        var original = _canvas.Flow;

        Assert.Equal(ConnectRefusal.None, _canvas.Drop("note", "bt", DropZone.RunTogether));

        var card = Card("note");
        Assert.Equal(["bt", "note"], card.Members);
        Assert.Contains(_canvas.Edges, e => e.From == card.Id && e.To == "out");
        Assert.Equal(FlowCanvasViewModel.DroppedFallbackNote, _canvas.TakeEditNote());
        Assert.Null(_canvas.TakeEditNote()); // said once
        Assert.Empty(DraftErrors());
        _canvas.Undo();
        Assert.Equal(original, _canvas.Flow);
    }

    [Fact]
    public void ALinkInsideACardIsStillRefused()
    {
        Assert.Equal(ConnectRefusal.None, _canvas.Drop("vol", "bt", DropZone.RunTogether));

        Assert.Equal(ConnectRefusal.SameCard, _canvas.Connect("bt", "vol"));
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
    public void AProblemMarksTheNodeWithADottedId()
    {
        _canvas.Edit(f => f with
        {
            Nodes = [.. f.Nodes, new StepNode("audio.x", "audio.setDefault"), new StepNode("audio", "wait") { With = new([KeyValuePair.Create("duration", (ParamValue)"1s")]) }],
        });

        Assert.True(Node("audio.x").HasProblem); // no device
        Assert.False(Node("audio").HasProblem);
    }

    [Fact]
    public void DeletedFlowShowsAsMissing()
    {
        _session.Update(c => c with { Flows = [] });

        Assert.True(_canvas.IsMissing);
        Assert.True(_canvas.IsEmpty);
    }

    // ---- T7.7: undo/redo, delete, copy/paste ----

    [Fact]
    public void UndoAndRedoStepThroughEdits()
    {
        Assert.False(_canvas.CanUndo);
        var original = _canvas.Flow;

        _canvas.Select("vol");
        _canvas.DeleteSelection();
        Assert.Null(_canvas.Find("vol"));
        Assert.True(_canvas.CanUndo);

        _canvas.Undo();
        Assert.Equal(original, _canvas.Flow);
        Assert.True(_canvas.CanRedo);
        Assert.False(_canvas.CanUndo);

        _canvas.Redo();
        Assert.Null(_canvas.Find("vol"));
        Assert.False(_canvas.CanRedo);
    }

    [Fact]
    public void ANewEditClearsRedoAndNoOpsAddNothing()
    {
        _canvas.MoveInColumn("out", 1);
        _canvas.Undo();
        Assert.True(_canvas.CanRedo);

        _canvas.Edit(f => f); // nothing changed: no entry, redo kept
        Assert.True(_canvas.CanRedo);
        Assert.False(_canvas.CanUndo);

        _canvas.TidyUp();
        _canvas.AddStep("wait");
        Assert.False(_canvas.CanRedo);
        Assert.True(_canvas.CanUndo);
    }

    [Fact]
    public void AnOutsideChangeClearsHistory()
    {
        _canvas.AddStep("wait");
        Assert.True(_canvas.CanUndo);

        _session.Update(c => c with { Flows = [.. c.Flows.Select(f => f with { Name = "Renamed elsewhere" })] });

        Assert.False(_canvas.CanUndo);
        Assert.Equal("Renamed elsewhere", _canvas.FlowName);
    }

    [Fact]
    public void AddStepRunsAfterTheSelectionAndSelectsIt()
    {
        var id = _canvas.AddStep("bluetooth.disconnect", ["bt"]);

        Assert.Equal("disconnect", id);
        Assert.Equal([new Edge("bt")], _canvas.Flow!.FindNode("disconnect")!.After);
        Assert.Equal(["disconnect"], _canvas.SelectedIds);
        Assert.Equal("disconnect", _canvas.FocusedId);
        Assert.Equal(Node("out").Column, Node("disconnect").Column);
    }

    [Fact]
    public void InsertStepOnAnEdge()
    {
        var edge = _canvas.Edges.Single(e => e.From == "bt" && e.To == "vol");

        var id = _canvas.InsertStep("wait", edge);

        Assert.Equal([new Edge("wait")], _canvas.Flow!.FindNode("vol")!.After);
        Assert.Equal([new Edge("bt")], _canvas.Flow!.FindNode(id!)!.After);
    }

    [Fact]
    public void DeleteReconnectsAndDropsTheSelection()
    {
        _canvas.Select("out");
        _canvas.DeleteSelection();

        Assert.Null(_canvas.Find("out"));
        Assert.Empty(_canvas.SelectedIds);
        Assert.Empty(_canvas.Flow!.FindNode("note")!.After); // "only if out fails" went with it
    }

    [Fact]
    public void CopyPasteRunsAfterTheSelectionAndDuplicateSitsBeside()
    {
        Assert.False(_canvas.CanPaste);
        _canvas.Select("vol");
        Assert.True(_canvas.Copy());
        Assert.True(_canvas.CanPaste);

        _canvas.Paste();
        Assert.Equal(["vol-2"], _canvas.SelectedIds);
        Assert.Equal([new Edge("vol")], _canvas.Flow!.FindNode("vol-2")!.After);

        _canvas.Select("vol");
        _canvas.Duplicate();
        Assert.Equal(["vol-3"], _canvas.SelectedIds);
        Assert.Equal([new Edge("bt")], _canvas.Flow!.FindNode("vol-3")!.After);
        Assert.Equal(Node("vol").Column, Node("vol-3").Column);

        _canvas.ClearSelection();
        Assert.False(_canvas.Copy());
    }

    [Fact]
    public void TheClipboardOutlivesTheCanvas()
    {
        var clipboard = new CanvasClipboard();
        using (var first = new FlowCanvasViewModel(_session, "f", clipboard))
        {
            first.Select("both");
            first.Copy();
        }
        using var second = new FlowCanvasViewModel(_session, "f", clipboard);
        second.ClearSelection();

        second.Paste();

        Assert.Equal(["both-2"], second.SelectedIds);
        Assert.Equal(["w1-2", "w2-2"], ((GroupNode)second.Flow!.FindNode("both-2")!).Members);
    }

    // ---- T7.8: outline ----

    [Fact]
    public void OutlineListsStepsInRunOrderWithCardsAsBranches()
    {
        var outline = _canvas.Outline;

        Assert.Equal("bt", outline[0].Id);
        Assert.Equal(["bt", "both", "out", "vol", "note"], outline.Select(i => i.Id));
        var card = outline.Single(i => i.Id == "both");
        Assert.Equal(["w1", "w2"], card.Children.Select(c => c.Id));
        Assert.Equal(Node("out").AutomationName, outline.Single(i => i.Id == "out").AutomationName);
        Assert.Contains("Waits for Connect", outline.Single(i => i.Id == "out").AutomationName, StringComparison.Ordinal);

        _canvas.Select("note");
        _canvas.DeleteSelection();
        Assert.DoesNotContain(_canvas.Outline, i => i.Id == "note");
    }

    // ---- T7.5: palette ----

    [Fact]
    public void PaletteGroupsByAreaAndFilters()
    {
        var palette = new StepPaletteViewModel();
        Assert.Contains(palette.Groups, g => g.Area == "Bluetooth" && g.Items.Any(i => i.Step == "bluetooth.connect"));

        palette.Query = "blue conn";
        var group = Assert.Single(palette.Groups);
        Assert.Equal(["bluetooth.connect", "bluetooth.disconnect"], group.Items.Select(i => i.Step));
        Assert.Equal("Connect, Bluetooth", group.Items[0].Name);

        palette.Query = "zzz";
        Assert.True(palette.IsEmpty);
    }

    [Fact]
    public void SearchPutsTitleMatchesFirst()
    {
        var results = StepPaletteViewModel.Search("connect");

        Assert.Equal("Connect", results[0].Title);
        Assert.Contains(results, r => r.Title == "Disconnect");
        Assert.Equal(StepPaletteViewModel.All.Count, StepPaletteViewModel.Search("  ").Count);
    }
    [Fact]
    public void ProblemsListThisFlowsProblemsErrorsFirstAndSelectTheirStep()
    {
        Assert.False(_canvas.HasProblems);
        Assert.Equal("Problems (0)", _canvas.ProblemsLabel);

        _session.Update(c => c with
        {
            Flows = [.. c.Flows.Select(f => f with
            {
                Hotkeys = ["Alt+"],
                Nodes = [.. f.Nodes.Select(n => n is StepNode { Id: "out" } s
                    ? s with { With = new([new KeyValuePair<string, ParamValue>("device", new ScalarParam("gone"))]) }
                    : n)],
            })],
        });

        Assert.Equal("Problems (2)", _canvas.ProblemsLabel);
        var step = Assert.Single(_canvas.Problems, p => p.NodeId == "out");
        Assert.Equal("Set default device", step.Where);
        Assert.Contains("gone", step.Message, StringComparison.Ordinal);
        var flow = Assert.Single(_canvas.Problems, p => p.NodeId is null);
        Assert.Equal("Flow settings", flow.Where);

        _canvas.Show(step);
        Assert.Equal(["out"], _canvas.SelectedIds);
        _canvas.Show(flow);
        Assert.Empty(_canvas.SelectedIds);
    }
}
