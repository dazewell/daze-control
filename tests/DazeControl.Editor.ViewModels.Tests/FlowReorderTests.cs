using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.FlowList;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T16.4: drop-position maths, keyboard/menu moves and the Undo stack (DESIGN B2-B4).</summary>
public sealed class FlowReorderTests : IDisposable
{
    // List: a [Audio] b d c e   (b, d in Audio)
    private const string Yaml = """
        version: 1
        flows:
          a: { name: A, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          b: { name: B, tray: { folder: Audio }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          c: { name: C, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          d: { name: D, tray: { folder: Audio }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          e: { name: E, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly ConfigSession _session;

    public FlowReorderTests()
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

    private static string Shown(FlowsPageViewModel page) =>
        string.Join(" ", page.Rows.Select(r => r switch { GroupItemViewModel g => $"[{g.Name}]", FlowItemViewModel f => f.Id, _ => "?" }));

    // Drop maths over the rows  a [Audio] b d c e  (index 0..5).
    private static readonly DropRow[] Rows =
    [
        new(false, "a", null), new(true, "Audio", null, "b"), new(false, "b", "Audio"), new(false, "d", "Audio"), new(false, "c", null), new(false, "e", null),
    ];

    private static DropTarget? Flow(int over, double f, bool inside = false) => DropMath.Resolve(Rows, over, f, inside, draggingGroup: false);

    [Fact]
    public void FlowOverATopLevelRowLandsBeforeOrAfterIt()
    {
        Assert.Equal(new DropTarget(null, "c", 4, false, null), Flow(4, 0.2));
        Assert.Equal(new DropTarget(null, "e", 5, false, null), Flow(4, 0.8));
        Assert.Equal(new DropTarget(null, null, 6, false, null), Flow(5, 0.8)); // below the last row
        Assert.Equal(new DropTarget(null, "b", 1, false, null), Flow(0, 0.8)); // before the group: its first member stands for it
    }

    [Fact]
    public void FlowOverAHeaderGoesBeforeItOrIntoItsGroup()
    {
        Assert.Equal(new DropTarget(null, "b", 1, false, null), Flow(1, 0.1));
        Assert.Equal(new DropTarget("Audio", null, -1, false, "Audio"), Flow(1, 0.5)); // middle: end of the group, header outlined
        Assert.Equal(new DropTarget("Audio", "b", 2, true, null), Flow(1, 0.9)); // bottom of an open header: first in the group
    }

    [Fact]
    public void BelowTheLastMemberThePointersXDecidesInsideOrAfterTheGroup()
    {
        Assert.Equal(new DropTarget("Audio", null, 4, true, null), Flow(3, 0.8, inside: true));
        Assert.Equal(new DropTarget(null, "c", 4, false, null), Flow(3, 0.8, inside: false));
        Assert.Equal(new DropTarget("Audio", "d", 3, true, null), Flow(2, 0.8)); // between members
        Assert.Equal(new DropTarget("Audio", "d", 3, true, null), Flow(3, 0.2)); // upper half of d
    }

    [Fact]
    public void GroupMovesAsABlockAmongTopLevelItems()
    {
        DropTarget? Group(int over, double f) => DropMath.Resolve(Rows, over, f, false, draggingGroup: true);
        Assert.Equal(new DropTarget(null, "c", 4, false, null), Group(3, 0.9)); // bottom of a member = after the group
        Assert.Equal(new DropTarget(null, "b", 1, false, null), Group(2, 0.1)); // top of a member = before the group
        Assert.Equal(new DropTarget(null, "b", 1, false, null), Group(0, 0.9)); // bottom of a top-level row = before the next unit
        Assert.Equal(new DropTarget(null, null, 6, false, null), Group(5, 0.9));
    }

    [Fact]
    public void NoRowNoTarget() => Assert.Null(DropMath.Resolve([], 0, 0.5, false, false));

    [Fact]
    public void DropIntoOutOfAndWithinAGroup()
    {
        using var page = Page();
        page.DropFlow("a", "Audio", "d"); // into the group, before d
        Assert.Equal("[Audio] b a d c e", Shown(page));
        page.DropFlow("a", null, null); // out, to the end
        Assert.Equal("[Audio] b d c e a", Shown(page));
        page.DropFlow("d", "Audio", "b"); // within
        Assert.Equal("[Audio] d b c e a", Shown(page));
    }

    [Fact]
    public void DropGroupMovesTheBlock()
    {
        using var page = Page();
        page.DropGroup("Audio", null);
        Assert.Equal("a c e [Audio] b d", Shown(page));
    }

    [Fact]
    public void DropOnItselfIsNoUndoStep()
    {
        using var page = Page();
        page.DropFlow("c", null, "c");
        page.DropFlow("a", null, "b"); // a stays before the group
        Assert.Equal("a [Audio] b d c e", Shown(page));
        Assert.False(page.CanUndo);
    }

    [Fact]
    public void KeyboardMoveStaysInItsContainer()
    {
        using var page = Page();
        page.MoveFlow("c", -1); // top level: above the group as a block
        Assert.Equal("a c [Audio] b d e", Shown(page));
        page.MoveFlow("c", 1);
        Assert.Equal("a [Audio] b d c e", Shown(page));
        page.MoveFlow("b", -1); // first in its group: stays
        Assert.Equal("a [Audio] b d c e", Shown(page));
        page.MoveFlow("b", 1);
        Assert.Equal("a [Audio] d b c e", Shown(page));
        page.MoveFlow("a", -1); // first overall
        Assert.Equal("a [Audio] d b c e", Shown(page));
        page.TakeOut("d"); // Alt+Left: just after the group
        Assert.Equal("a [Audio] b d c e", Shown(page));
        Assert.Null(page.Items.Single(i => i.Id == "d").Group); // d now stands after the group, not in it
    }

    [Fact]
    public void UndoRestoresStepByStep()
    {
        using var page = Page();
        page.DropGroup("Audio", null);
        page.MoveFlow("a", 1);
        Assert.True(page.CanUndo);
        page.UndoCommand.Execute(null);
        Assert.Equal("a c e [Audio] b d", Shown(page));
        page.UndoCommand.Execute(null);
        Assert.Equal("a [Audio] b d c e", Shown(page));
        Assert.False(page.CanUndo);
        Assert.False(page.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void UndoKeepsTwentySteps()
    {
        using var page = Page();
        for (var i = 0; i < 25; i++)
            page.MoveFlow("c", i % 2 == 0 ? 1 : -1);
        var steps = 0;
        while (page.CanUndo)
        {
            page.UndoCommand.Execute(null);
            steps++;
        }
        Assert.Equal(20, steps);
    }

    [Fact]
    public void LeavingOrAnOutsideChangeClearsTheStack()
    {
        using var page = Page();
        page.MoveFlow("c", -1);
        Assert.True(page.CanUndo);
        page.ClearUndo(); // opening a flow / leaving the page
        Assert.False(page.CanUndo);

        page.MoveFlow("c", -1);
        _session.Update(c => c with { Flows = [.. c.Flows.Reverse()] }); // not made through the page: stale text must not come back
        Assert.False(page.CanUndo);
    }

    [Fact]
    public void TheGripSaysWhyItIsOffWhenReadOnly()
    {
        using var page = Page();
        Assert.Equal("Drag to reorder", page.Items[0].DragTip);
    }
}
