using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.FlowList;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T16.6: the selection rules and each bulk action as one Undo step (DESIGN Part F, D-090).</summary>
public sealed class FlowSelectionTests : IDisposable
{
    // List: a [Audio] b d c e   (b, d in Audio); e calls a.
    private const string Yaml = """
        version: 1
        flows:
          a: { name: A, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          b: { name: B, tray: { folder: Audio }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          c: { name: C, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          d: { name: D, tray: { folder: Audio }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          e: { name: E, nodes: [ { id: call, step: flow.run, with: { flow: a } } ] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly ConfigSession _session;

    public FlowSelectionTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, a => a());
        _session.Load();
    }

    public void Dispose()
    {
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private FlowsPageViewModel Page() => new(_session, _engine, a => a());

    private static string Selected(FlowsPageViewModel page) => string.Join(" ", page.SelectedIds);

    private string Order() => string.Join(" ", _session.Current.Flows.Select(f => f.Id + (f.Tray.Folder is { } g ? $"@{g}" : "")));

    [Fact]
    public void ToggleAddsAndRemovesAndShowsOnTheCard()
    {
        using var page = Page();
        page.ToggleSelected("c");
        page.ToggleSelected("e");
        Assert.Equal("c e", Selected(page));
        Assert.Equal("2 selected", page.SelectionText);
        Assert.True(page.Items.Single(i => i.Id == "c").IsSelected);
        page.ToggleSelected("c");
        Assert.Equal("e", Selected(page));
        Assert.False(page.Items.Single(i => i.Id == "c").IsSelected);
    }

    [Fact]
    public void RangeCoversVisibleRowsOnly()
    {
        using var page = Page();
        // Shown: a [Audio] b d c e. Rows, not group headers, count: a b d c.
        page.ToggleSelected("a");
        page.SelectRange("c");
        Assert.Equal("a b c d", Selected(page)); // in file order

        page.ClearSelection();
        page.ToggleGroup("Audio"); // collapsed: b and d are not on screen
        page.ToggleSelected("a");
        page.SelectRange("c");
        Assert.Equal("a c", Selected(page));
    }

    [Fact]
    public void RangeWithoutAnAnchorSelectsJustThatRow()
    {
        using var page = Page();
        page.SelectRange("c");
        Assert.Equal("c", Selected(page));
    }

    [Fact]
    public void RangeRunsBackwardsToo()
    {
        using var page = Page();
        page.ToggleSelected("e");
        page.SelectRange("c");
        Assert.Equal("c e", Selected(page));
    }

    [Fact]
    public void SelectAllTakesWhatIsOnScreen()
    {
        using var page = Page();
        page.ToggleGroup("Audio");
        page.SelectAll();
        Assert.Equal("a c e", Selected(page));

        page.ClearSelection();
        page.SearchText = "Audio";
        page.SelectAll();
        Assert.Equal("b d", Selected(page));
    }

    [Fact]
    public void AGroupHeaderSelectsItsMembersEvenCollapsedAndTogglesThemOff()
    {
        using var page = Page();
        page.ToggleGroup("Audio");
        page.ToggleGroupSelected("Audio");
        Assert.Equal("b d", Selected(page));
        Assert.True(page.Rows.OfType<GroupItemViewModel>().Single().IsSelected);

        page.ToggleGroupSelected("Audio");
        Assert.Equal("", Selected(page));
    }

    [Fact]
    public void ASearchDropsWhatItHidesFromTheSelection()
    {
        using var page = Page();
        page.ToggleSelected("a");
        page.ToggleSelected("d");
        page.SearchText = "Audio";
        Assert.Equal("d", Selected(page));
    }

    [Fact]
    public void TurningSelectModeOffClearsAndTheCardsShowCheckboxesInIt()
    {
        using var page = Page();
        page.SelectMode = true;
        Assert.All(page.Items, i => Assert.True(i.ShowCheck));
        page.ToggleSelected("a");
        page.SelectMode = false;
        Assert.False(page.HasSelection);
        Assert.All(page.Items, i => Assert.False(i.ShowCheck));
    }

    [Fact]
    public void TurnOffAndOnAreOneUndoStep()
    {
        using var page = Page();
        page.ToggleSelected("a");
        page.ToggleSelected("c");
        page.ToggleSelected("d");
        page.SetSelectedEnabled(false);
        Assert.Equal(["b", "e"], _session.Current.Flows.Where(f => f.Enabled).Select(f => f.Id));

        page.UndoCommand.Execute(null);
        Assert.All(_session.Current.Flows, f => Assert.True(f.Enabled));
        Assert.False(page.CanUndo);
    }

    [Fact]
    public void GroupAndMoveToAreOneUndoStepEach()
    {
        using var page = Page();
        page.ToggleSelected("c");
        page.ToggleSelected("e");
        page.MoveToGroup(page.SelectedIds, "TV");
        Assert.Equal("a b@Audio d@Audio c@TV e@TV", Order());
        page.MoveToGroup(page.SelectedIds, "Audio");
        Assert.Equal("a b@Audio d@Audio c@Audio e@Audio", Order());
        page.MoveToGroup(page.SelectedIds, null);
        Assert.Equal("a b@Audio d@Audio c e", Order());

        page.UndoCommand.Execute(null);
        Assert.Equal("a b@Audio d@Audio c@Audio e@Audio", Order());
        page.UndoCommand.Execute(null);
        Assert.Equal("a b@Audio d@Audio c@TV e@TV", Order());
        page.UndoCommand.Execute(null);
        Assert.Equal("a b@Audio c d@Audio e", Order());
    }

    [Fact]
    public void DeleteIsOneBarAndOneUndo()
    {
        using var page = Page();
        page.ToggleSelected("c");
        page.ToggleSelected("d");
        page.DeleteSelected();
        Assert.Equal("a b@Audio e", Order());
        Assert.Equal("2 flows were deleted.", page.DeletedText);
        Assert.False(page.HasSelection);

        page.UndoDeleteCommand.Execute(null);
        Assert.Equal("a b@Audio c d@Audio e", Order());
        Assert.False(page.CanUndoDelete);
        Assert.False(page.CanUndo);
    }

    [Fact]
    public void CtrlZAfterABulkDeleteIsTheSameStepAsTheBar()
    {
        using var page = Page();
        page.ToggleSelected("c");
        page.ToggleSelected("d");
        page.DeleteSelected();
        page.UndoCommand.Execute(null);
        Assert.Equal("a b@Audio c d@Audio e", Order());
        Assert.False(page.CanUndoDelete);
    }

    [Fact]
    public void DeleteIsOffWhileAnotherFlowRunsOneOfThem()
    {
        using var page = Page();
        page.ToggleSelected("a"); // e runs a
        page.ToggleSelected("c");
        Assert.False(page.CanBulkDelete);
        Assert.Equal("E runs one of these flows. Remove that step first.", page.BulkDeleteOffReason);
        page.DeleteSelected();
        Assert.Contains(_session.Current.Flows, f => f.Id == "a");

        page.ToggleSelected("e"); // the caller goes too: fine
        Assert.True(page.CanBulkDelete);
        Assert.Null(page.BulkDeleteOffReason);
        page.DeleteSelected();
        Assert.Equal("b@Audio d@Audio", Order());
    }

    [Fact]
    public void ASelectedCardDragsTheWholeSelectionAndAnUnselectedOneDragsAlone()
    {
        using var page = Page();
        page.ToggleSelected("c");
        page.ToggleSelected("e");
        page.DropFlow("c", null, "a");
        Assert.Equal("c e a b@Audio d@Audio", Order());
        Assert.True(page.CanUndo);

        page.UndoCommand.Execute(null);
        page.DropFlow("a", null, null); // a is not selected: only a moves
        Assert.Equal("b@Audio d@Audio c e a", Order());
    }

    [Fact]
    public void ActionIdsAreTheSelectionOnlyWhenTheCardIsInIt()
    {
        using var page = Page();
        page.ToggleSelected("c");
        page.ToggleSelected("e");
        Assert.Equal(["c", "e"], page.ActionIds("e"));
        Assert.Equal(["a"], page.ActionIds("a"));
    }

    [Fact]
    public void BulkActionsAreOffWhenTheFileIsReadOnlyOrSorted()
    {
        using var page = Page();
        page.ToggleSelected("a");
        Assert.True(page.CanBulkMove);
        page.Sort = FlowSort.Name;
        Assert.False(page.CanBulkMove); // grouping reorders: only in Your order
        Assert.True(page.CanBulkEdit);
    }
}
