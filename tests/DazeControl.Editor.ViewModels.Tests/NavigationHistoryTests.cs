using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Back/forward through the editor's places (D-095).</summary>
public sealed class NavigationHistoryTests : IDisposable
{
    private const string Yaml = """
        version: 1
        flows:
          a:
            name: A
            nodes:
              - { id: wait, step: wait, with: { duration: 1s } }
          b:
            name: B
            nodes:
              - { id: wait, step: wait, with: { duration: 1s } }
        """;

    private static readonly Func<EditorLocation, bool> All = _ => true;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ShellViewModel _shell;
    private readonly ConfigSession _session;

    public NavigationHistoryTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, a => a());
        _session.Load();
        _shell = new ShellViewModel(_session, new FakeEngine { IsRunning = true }, a => a(), _folder);
    }

    public void Dispose()
    {
        _shell.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void BackAndForwardWalkThePagesAndFlows()
    {
        _shell.OpenFlow("a");
        _shell.CloseFlow();
        _shell.Page = EditorPage.Settings;

        Assert.Equal(new EditorLocation(EditorPage.Flows), _shell.Target(false));
        _shell.Go(false);
        Assert.Equal(EditorPage.Flows, _shell.Page);
        Assert.Null(_shell.Editor);
        _shell.Go(false);
        Assert.Equal("a", _shell.Editor?.FlowId);
        _shell.Go(false);
        Assert.False(_shell.CanGoBack);

        _shell.Go(true);
        _shell.Go(true);
        _shell.Go(true);
        Assert.Equal(EditorPage.Settings, _shell.Page);
        Assert.Null(_shell.Editor);
        Assert.False(_shell.CanGoForward);
    }

    [Fact]
    public void OpeningAnotherFlowIsOneStep()
    {
        _shell.OpenFlow("a");
        _shell.OpenFlow("b"); // closes a on the way: no list in between

        _shell.Go(false);

        Assert.Equal("a", _shell.Editor?.FlowId);
    }

    [Fact]
    public void ANewPlaceAfterBackDropsForward()
    {
        _shell.Page = EditorPage.Runs;
        _shell.Go(false);
        _shell.Page = EditorPage.About;

        Assert.False(_shell.CanGoForward);
        _shell.Go(false);
        Assert.Equal(EditorPage.Flows, _shell.Page);
    }

    [Fact]
    public void ADeletedFlowIsSkipped()
    {
        _shell.OpenFlow("a");
        _shell.CloseFlow();
        _shell.OpenFlow("b");
        _shell.CloseFlow();
        _session.Update(c => c with { Flows = [.. c.Flows.Where(f => f.Id != "b")] });

        Assert.Equal(new EditorLocation(EditorPage.Flows), _shell.Target(false)); // b's entry is skipped
        _shell.Go(false);
        _shell.Go(false);
        Assert.Equal("a", _shell.Editor?.FlowId);
    }

    [Fact]
    public void RenamedFlowKeepsItsPlaces()
    {
        var h = new NavigationHistory();
        h.Visit(new(EditorPage.Flows, "a"));
        h.Visit(new(EditorPage.Runs));

        h.Rename("a", "z");

        Assert.Equal(new EditorLocation(EditorPage.Flows, "z"), h.PeekBack(All));
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var h = new NavigationHistory();
        for (var i = 0; i < 200; i++)
            h.Visit(new(EditorPage.Flows, "f" + i));
        var steps = 0;
        while (h.PeekBack(All) is not null)
        {
            h.CommitBack();
            steps++;
        }
        Assert.Equal(NavigationHistory.Limit, steps);
    }

    [Fact]
    public void RepeatingThePlaceAddsNothing()
    {
        var h = new NavigationHistory();
        h.Visit(new(EditorPage.Flows));
        h.Visit(new(EditorPage.Library));
        h.Visit(new(EditorPage.Library));

        Assert.True(h.CanGoBack);
        h.CommitBack();
        Assert.False(h.CanGoBack);
    }
}
