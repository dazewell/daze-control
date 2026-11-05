using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T16.3: the Flows list as group headers + flows (D-087), and the group menu's actions.</summary>
public sealed class GroupedListTests : IDisposable
{
    private const string Yaml = """
        version: 1
        flows:
          a:
            name: A
            nodes: [ { id: w, step: wait, with: { duration: 1s } } ]
          b:
            name: B
            tray: { folder: Audio }
            nodes: [ { id: w, step: wait, with: { duration: 1s } } ]
          c:
            name: C
            nodes: [ { id: w, step: wait, with: { duration: 1s } } ]
          d:
            name: D
            tray: { folder: audio }
            nodes: [ { id: w, step: wait, with: { duration: 1s } } ]
          e:
            name: E
            nodes: [ { id: w, step: wait, with: { duration: 1s } } ]
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly ConfigSession _session;

    public GroupedListTests()
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

    private FlowsPageViewModel Page(EditorSettingsStore? settings = null) => new(_session, _engine, a => a(), settings: settings);

    private static string Shown(FlowsPageViewModel page) =>
        string.Join(" ", page.Rows.Select(r => r switch { GroupItemViewModel g => $"[{g.Name}]", FlowItemViewModel f => f.Id, _ => "?" }));

    private static GroupItemViewModel Header(FlowsPageViewModel page) => page.Rows.OfType<GroupItemViewModel>().Single();

    [Fact]
    public void RowsAreTheTrayOrderWithAHeaderAtTheFirstMemberAndMembersTogether()
    {
        using var page = Page();

        Assert.Equal("a [Audio] b d c e", Shown(page));
        Assert.Equal(["a", "b", "c", "d", "e"], page.Items.Select(i => i.Id)); // Items stays the file order
        Assert.Equal([false, true, false, true, false], page.Items.Select(i => i.InGroup));
    }

    [Fact]
    public void HeaderSaysHowManyFlowsAndHowManyRun()
    {
        using var page = Page();
        var header = Header(page);

        Assert.Equal("2 flows", header.Caption);
        Assert.Equal("Audio, group, 2 flows, expanded", header.Summary);
        Assert.Equal("Collapse Audio", header.ToggleName);

        page.Items.Single(i => i.Id == "b").UpdateRun(engineRunning: true, running: true);
        header.Refresh();
        Assert.Equal("2 flows · 1 running", header.Caption);
    }

    [Fact]
    public void CollapsingHidesTheMembersAndIsRememberedPerPc()
    {
        var path = Path.Combine(_folder, "editor.json");
        using (var store = new EditorSettingsStore(path, _time))
        {
            using var page = Page(store);
            page.ToggleGroup("Audio");

            Assert.Equal("a [Audio] c e", Shown(page));
            Assert.False(Header(page).IsExpanded);
            Assert.Equal("Audio, group, 2 flows, collapsed", Header(page).Summary);
            Assert.Equal("Expand Audio", Header(page).ToggleName);
            Assert.Equal(["Audio"], store.Settings.FlowList.CollapsedGroups);
        }

        using var again = new EditorSettingsStore(path, _time);
        using var reopened = Page(again);
        Assert.Equal("a [Audio] c e", Shown(reopened));

        reopened.ToggleGroup("audio"); // folder names compare like the tray's
        Assert.Equal("a [Audio] b d c e", Shown(reopened));
        Assert.Empty(again.Settings.FlowList.CollapsedGroups);
    }

    [Fact]
    public void ARunStateChangeDoesNotRebuildTheRows()
    {
        using var page = Page();
        var before = page.Rows.ToList();

        page.Items[0].IsEnabled = false; // a switch click: the file changes, the rows stay the same objects

        Assert.Equal(before, page.Rows);
    }

    [Fact]
    public void RenameUngroupAndTurnAllOffEditTheFlows()
    {
        using var page = Page();

        page.SetGroupEnabled("Audio", false);
        Assert.False(_session.Current.Flows.Single(f => f.Id == "b").Enabled);
        Assert.False(_session.Current.Flows.Single(f => f.Id == "d").Enabled);
        Assert.True(_session.Current.Flows.Single(f => f.Id == "a").Enabled);
        page.SetGroupEnabled("Audio", true);
        Assert.True(_session.Current.Flows.Single(f => f.Id == "d").Enabled);

        page.RenameGroup("Audio", "  Sound ");
        Assert.Equal("a [Sound] b d c e", Shown(page));
        page.RenameGroup("Sound", "   "); // not a name: nothing changes
        Assert.Equal("a [Sound] b d c e", Shown(page));

        page.Ungroup("Sound");
        Assert.Equal("a b d c e", Shown(page));
        Assert.All(_session.Current.Flows, f => Assert.Null(f.Tray.Folder));
    }

    [Fact]
    public void RenamingACollapsedGroupKeepsItCollapsed()
    {
        using var page = Page();
        page.ToggleGroup("Audio");

        page.RenameGroup("Audio", "Sound");

        Assert.Equal("a [Sound] c e", Shown(page));
    }

    [Fact]
    public void GroupMovesUpAndDownAmongTheTopLevelItems()
    {
        using var page = Page();
        Assert.True(Header(page).CanMoveUp);
        Assert.True(Header(page).CanMoveDown);

        page.MoveGroup("Audio", -1);
        Assert.Equal("[Audio] b d a c e", Shown(page));
        Assert.False(Header(page).CanMoveUp);

        page.MoveGroup("Audio", -1); // already first: nothing
        Assert.Equal("[Audio] b d a c e", Shown(page));

        page.MoveGroup("Audio", 1);
        page.MoveGroup("Audio", 1);
        page.MoveGroup("Audio", 1);
        Assert.Equal("a c e [Audio] b d", Shown(page));
        Assert.False(Header(page).CanMoveDown);
    }

    [Fact]
    public void MoveToPutsAFlowIntoAnExistingOrNewGroupOrOutOfItsGroup()
    {
        using var page = Page();

        page.MoveFlowToGroup("a", "Audio");
        Assert.Equal("[Audio] b d a c e", Shown(page));

        page.MoveFlowToGroup("c", "Lights");
        Assert.Equal(["Audio", "Lights"], page.GroupNames);
        Assert.Equal("[Audio] b d a [Lights] c e", Shown(page));

        page.MoveFlowToGroup("b", null);
        Assert.Equal("[Audio] d a b [Lights] c e", Shown(page));
        Assert.Null(page.Items.Single(i => i.Id == "b").Group);
    }

    [Fact]
    public void MovingToTheCurrentGroupChangesNothing()
    {
        using var page = Page();
        page.ToggleGroup("Audio");
        var before = _session.Current;

        page.MoveFlowToGroup("b", "audio");
        page.MoveFlowToGroup("a", null);

        Assert.Same(before, _session.Current);
        Assert.False(Header(page).IsExpanded);
    }

    [Fact]
    public void MovingAFlowIntoACollapsedGroupExpandsIt()
    {
        using var page = Page();
        page.ToggleGroup("Audio");

        page.MoveFlowToGroup("a", "Audio");

        Assert.True(Header(page).IsExpanded);
        Assert.Contains("a", Shown(page).Split(' '));
    }
}
