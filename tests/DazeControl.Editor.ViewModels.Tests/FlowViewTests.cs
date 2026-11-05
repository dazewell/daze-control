using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.FlowList;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T16.5: sort, search and filter are view-only; "Make this my order" writes the sort (DESIGN Part E, D-088).</summary>
public sealed class FlowViewTests : IDisposable
{
    // File order: a [Audio: b d] c e.  Names: a=Zed b=Mid c=Alpha d=Beta e=Echo.
    private const string Yaml = """
        version: 1
        flows:
          a: { name: Zed, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          b: { name: Mid, tray: { folder: Audio }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          c: { name: Alpha, tray: { show: false }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          d: { name: Beta, enabled: false, tray: { folder: Audio }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          e: { name: Echo, hotkeys: [ "Alt+Q" ], nodes: [ { id: w, name: Pause here, step: wait, with: { duration: 1s } } ] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly ConfigSession _session;

    public FlowViewTests()
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

    private string Saved() => string.Join(" ", _session.Current.Flows.Select(f => f.Id));

    [Fact]
    public void NameSortKeepsGroupsInPlaceAndIsViewOnly()
    {
        using var page = Page();
        var before = Saved();
        page.Sort = FlowSort.Name;
        Assert.Equal("c [Audio] d b e a", Shown(page)); // top-level flows swap among their places, members sort inside the group
        Assert.Equal(before, Saved());
        Assert.False(page.CanReorder);
        Assert.True(page.CanMakeOrder);
    }

    [Fact]
    public void SearchMatchesNameShortcutGroupAndStepsAndOpensGroups()
    {
        using var page = Page();
        page.ToggleGroup("Audio");
        Assert.Equal("a [Audio] c e", Shown(page));

        page.SearchText = "alt+q";
        Assert.Equal("e", Shown(page));
        page.SearchText = "pause"; // a step's own label
        Assert.Equal("e", Shown(page));
        page.SearchText = "audio"; // the group's name matches its members; collapsed groups open
        Assert.Equal("[Audio] b d", Shown(page));
        page.SearchText = "beta";
        Assert.Equal("[Audio] d", Shown(page));
        page.SearchText = "wait"; // a step's name
        Assert.Equal("a [Audio] b d c e", Shown(page));
        page.SearchText = "";
        Assert.Equal("a [Audio] c e", Shown(page)); // collapsed again
    }

    [Fact]
    public void FiltersShowOnOffAndNotInTray()
    {
        using var page = Page();
        page.Filter = FlowFilter.Off;
        Assert.Equal("[Audio] d", Shown(page));
        page.Filter = FlowFilter.NotInTray;
        Assert.Equal("c", Shown(page));
        page.Filter = FlowFilter.On;
        Assert.Equal("a [Audio] b c e", Shown(page));
        page.Filter = FlowFilter.NeedsFixing;
        Assert.True(page.NoMatches);
    }

    [Fact]
    public void ReorderIsOffWhileSortedSearchedOrFiltered()
    {
        using var page = Page();
        var before = Saved();
        Assert.True(page.CanReorder);
        page.SearchText = "e";
        page.DropFlow("a", null, "e");
        page.MoveFlow("e", -1);
        page.MoveFlowToGroup("a", "Audio");
        Assert.Equal(before, Saved());
        Assert.Contains("Clear", page.ReorderOffReason);
        page.SearchText = "";
        page.Sort = FlowSort.Name;
        page.MoveFlow("e", -1);
        Assert.Equal(before, Saved());
        Assert.StartsWith("Sorted by name", page.ReorderOffReason);
        page.Sort = FlowSort.Yours;
        page.MoveFlow("e", -1);
        Assert.NotEqual(before, Saved());
    }

    [Fact]
    public void MakeThisMyOrderWritesTheVisibleOrderAsOneUndo()
    {
        using var page = Page();
        page.Sort = FlowSort.Name;
        var shown = Shown(page);
        var before = Saved();
        page.MakeOrderCommand.Execute(null);
        Assert.Equal(FlowSort.Yours, page.Sort);
        Assert.Equal(shown, Shown(page));
        Assert.NotEqual(before, Saved());
        page.UndoCommand.Execute(null);
        Assert.Equal(before, Saved());
    }

    [Fact]
    public void EmptyResultOffersClearAndKeepsTheSort()
    {
        using var page = Page();
        page.Sort = FlowSort.Name;
        page.SearchText = "zzz";
        Assert.True(page.NoMatches);
        page.ClearFilterCommand.Execute(null);
        Assert.False(page.NoMatches);
        Assert.Equal("", page.SearchText);
        Assert.Equal(FlowSort.Name, page.Sort);
    }

    [Fact]
    public void SortAndFilterAreRememberedButSearchIsNot()
    {
        var path = Path.Combine(_folder, "editor.json");
        using (var store = new EditorSettingsStore(path))
        {
            using var page = Page(store);
            page.Sort = FlowSort.LastRun;
            page.Filter = FlowFilter.On;
            page.SearchText = "x";
            store.Flush();
        }
        using var again = new EditorSettingsStore(path);
        using var next = Page(again);
        Assert.Equal(FlowSort.LastRun, next.Sort);
        Assert.Equal(FlowFilter.On, next.Filter);
        Assert.Equal("", next.SearchText);
    }
}
