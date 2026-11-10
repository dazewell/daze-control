using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>The Layout menu (D-067): per-flow choices in editor.json by flow id, not flow edits; Untangle is one.</summary>
public sealed class LayoutMenuTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private EditorSettingsStore _store;
    private FlowCanvasViewModel _canvas;

    public LayoutMenuTests()
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

    /// <summary>Closes and reopens the editor: the store writes on dispose and reads once at start.</summary>
    private void Restart()
    {
        _canvas.Dispose();
        _store.Dispose();
        _store = new EditorSettingsStore(SettingsPath, _time);
        _canvas = new FlowCanvasViewModel(_session, _canvas.FlowId, settings: _store);
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _store.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void ANewFlowHasTheDefaultLayoutAndNothingStored()
    {
        Assert.True(_canvas.LayoutChoices.IsDefault);
        Assert.Equal(LayoutOptions.Default, _canvas.LayoutOptions);
        Assert.Empty(_store.Settings.Flows);
    }

    [Fact]
    public void ADroppedStepStaysWhereItWasPutSnappedToTheGridAndIsNotAFlowEdit()
    {
        var bt = (Node("bt").X, Node("bt").Y);

        Assert.True(_canvas.PlaceAt("out", new Point(403, 317)));

        Assert.Equal((400, 320), (Node("out").X, Node("out").Y));
        Assert.Equal(bt, (Node("bt").X, Node("bt").Y));
        Assert.True(_canvas.IsPlaced("out"));
        Assert.False(_session.HasUnsavedChanges);
        Assert.False(_canvas.CanUndo);

        Restart();

        Assert.Equal((400, 320), (Node("out").X, Node("out").Y));
    }

    [Fact]
    public void PutBackInLineAndResetLayoutLetTheLayoutPlaceAStepAgain()
    {
        var auto = (Node("out").X, Node("out").Y);
        _canvas.PlaceAt("out", new Point(400, 320));

        _canvas.Unplace(["out", "bt"]);

        Assert.Equal(auto, (Node("out").X, Node("out").Y));
        Assert.Empty(_store.Settings.Flows);

        _canvas.PlaceAt("out", new Point(400, 320));
        Assert.False(_canvas.LayoutChoices.IsDefault);
        _canvas.ResetLayout();

        Assert.Equal(auto, (Node("out").X, Node("out").Y));
        Assert.Empty(_store.Settings.Flows);
    }

    [Fact]
    public void PickingAnArrangementLaysEverythingOutAfreshAndIsRemembered()
    {
        _canvas.PlaceAt("out", new Point(400, 320));

        _canvas.SetLayout(shape: LayoutShape.Circle);

        Assert.False(_canvas.IsPlaced("out"));
        Assert.Equal(LayoutShape.Circle, _canvas.LayoutOptions.Shape);
        Assert.False(_session.HasUnsavedChanges);

        Restart();

        Assert.Equal(LayoutShape.Circle, _canvas.LayoutOptions.Shape);
        _canvas.SetLayout(shape: LayoutShape.Columns);
        Assert.Empty(_store.Settings.Flows);
    }

    [Fact]
    public void AStepThatIsNotOnTheCanvasCannotBePlaced()
    {
        Assert.False(_canvas.PlaceAt("nope", new Point(10, 10)));
        Assert.Empty(_store.Settings.Flows);
    }

    [Fact]
    public void AChoiceLaysTheFlowOutAgainAtOnceAndIsNotAFlowEdit()
    {
        var column = Node("out").X - Node("bt").X;

        _canvas.SetLayout(spacing: LayoutSpacing.Roomy);

        Assert.Equal(column + 96 - 64, Node("out").X - Node("bt").X); // one column gap wider
        Assert.Equal(LayoutSpacing.Roomy, _store.Settings.Flows["f"].Spacing);
        Assert.False(_session.HasUnsavedChanges);
        Assert.False(_canvas.CanUndo);
    }

    [Fact]
    public void RightAnglesMakeEveryEdgeOrthogonal()
    {
        Assert.All(_canvas.Edges, e => Assert.False(e.Curve.IsOrthogonal));

        _canvas.SetLayout(lines: LayoutLines.RightAngles);

        Assert.All(_canvas.Edges, e => Assert.True(e.Curve.IsOrthogonal));
    }

    [Fact]
    public void ChoicesSurviveARestart()
    {
        _canvas.SetLayout(wrapAfter: 0, spacing: LayoutSpacing.Compact, align: LayoutAlign.Top, lines: LayoutLines.RightAngles);
        var placed = _canvas.Nodes.ToDictionary(n => n.Id, n => n.Bounds);

        Restart();

        Assert.Equal(LayoutOptions.Of(0, LayoutSpacing.Compact, LayoutAlign.Top, LayoutLines.RightAngles), _canvas.LayoutOptions);
        Assert.All(_canvas.Nodes, n => Assert.Equal(placed[n.Id], n.Bounds));
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"spacing\": \"Compact\"", json);
        Assert.Contains("\"lines\": \"RightAngles\"", json);
    }

    [Fact]
    public void ADefaultChoiceIsNotStoredAndResetForgetsTheFlow()
    {
        _canvas.SetLayout(spacing: LayoutSpacing.Normal, wrapAfter: LayoutOptions.DefaultWrapAfter);
        Assert.Empty(_store.Settings.Flows);

        _canvas.SetLayout(align: LayoutAlign.Top);
        Assert.False(_canvas.LayoutChoices.IsDefault);
        _canvas.SetLayout(align: LayoutAlign.Centre);
        Assert.Empty(_store.Settings.Flows);

        _canvas.SetLayout(spacing: LayoutSpacing.Roomy, lines: LayoutLines.RightAngles);
        _canvas.ResetLayout();

        Assert.Empty(_store.Settings.Flows);
        Assert.Equal(LayoutOptions.Default, _canvas.LayoutOptions);
        Assert.All(_canvas.Edges, e => Assert.False(e.Curve.IsOrthogonal));
    }

    [Fact]
    public void ChoicesAreKeptPerFlow()
    {
        _canvas.SetLayout(spacing: LayoutSpacing.Roomy);

        using var other = new FlowCanvasViewModel(_session, "g", settings: _store);

        Assert.True(other.LayoutChoices.IsDefault);
    }

    [Fact]
    public void ChoicesFollowASavedRename()
    {
        _canvas.SetLayout(spacing: LayoutSpacing.Roomy);
        var column = Node("out").X - Node("bt").X;
        _session.UpdateDraft("f", f => f with { Name = "Evening" });

        Assert.True(_canvas.Save());

        Assert.Equal("evening", _canvas.FlowId);
        Assert.Equal(LayoutSpacing.Roomy, _store.Settings.Flows["evening"].Spacing);
        Assert.False(_store.Settings.Flows.ContainsKey("f"));
        Assert.Equal(column, Node("out").X - Node("bt").X);
        Restart();
        Assert.Equal(96, _canvas.LayoutOptions.ColumnGap);
    }

    [Fact]
    public void UntangleIsOffWithItsReasonWhenNoLinesCross()
    {
        Assert.False(_canvas.CanUntangle);
        Assert.Equal("No lines cross", _canvas.UntangleReason);
        Assert.False(_canvas.Untangle());
    }

    private static readonly string[] Crossed = ["bt", "vol", "out", "note", "both", "w1", "w2"];

    /// <summary>vol also waits for the card and sits above out: bt→out and both→vol cross.</summary>
    private void Cross() => _canvas.Edit(f => f with
    {
        Nodes = [.. Crossed.Select(id =>
            f.FindNode(id) is StepNode { Id: "vol" } vol ? vol with { After = [.. vol.After, new Edge("both")] } : f.FindNode(id)!)],
    });

    [Fact]
    public void UntangleIsAFlowEditThatUndoes()
    {
        Cross();
        Assert.True(_canvas.CanUntangle);
        Assert.Null(_canvas.UntangleReason);

        Assert.True(_canvas.Untangle());

        Assert.Equal(["bt", "out", "vol", "note", "both", "w1", "w2"], _canvas.Flow!.Nodes.Select(n => n.Id));
        Assert.Equal("No lines cross", _canvas.UntangleReason);
        _canvas.Undo();
        Assert.Equal(Crossed, _canvas.Flow!.Nodes.Select(n => n.Id));
        Assert.True(_canvas.CanUntangle);
    }

    [Fact]
    public void UntangleIsOffWhileTheFlowCantBeEdited()
    {
        Cross();

        _canvas.IsRunLocked = true;

        Assert.Equal(UntangleState.Helps, _canvas.UntangleState);
        Assert.False(_canvas.CanUntangle);
        Assert.False(_canvas.Untangle()); // the menu was open when the run started: nothing changes, no "picked"
        Assert.Equal(Crossed, _canvas.Flow!.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void ResetKeepsKeysANewerEditorWrote()
    {
        _canvas.SetLayout(spacing: LayoutSpacing.Roomy);
        _store.Update(s => s.Flows["f"].Other = new() { ["zoom"] = System.Text.Json.JsonDocument.Parse("1.5").RootElement });

        _canvas.ResetLayout();

        Assert.Equal(LayoutOptions.Default, _canvas.LayoutOptions);
        Assert.True(_store.Settings.Flows["f"].Other!.ContainsKey("zoom"));
    }
}
