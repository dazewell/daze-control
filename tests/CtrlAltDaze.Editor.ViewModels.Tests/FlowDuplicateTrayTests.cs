using CtrlAltDaze.Editor.ViewModels.Config;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>T16.7: Duplicate and the tray toggles from the Flows page, each one Undo step (DESIGN Part F, D-090).</summary>
public sealed class FlowDuplicateTrayTests : IDisposable
{
    private const string Yaml = """
        version: 1
        flows:
          a: { name: A, hotkeys: [Alt+A], nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          b: { name: B, hotkeys: [Alt+B], tray: { folder: Audio, separator: true }, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
          c: { name: C, nodes: [ { id: w, step: wait, with: { duration: 1s } } ] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly ConfigSession _session;

    public FlowDuplicateTrayTests()
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

    private string Order() => string.Join(" ", _session.Current.Flows.Select(f => f.Id));

    [Fact]
    public void DuplicateLandsAfterTheOriginalWithoutShortcutsAndUndoesInOneStep()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        var made = page.Duplicate(["a"]);
        Assert.Equal(["a-copy"], made);
        Assert.Equal("a a-copy b c", Order());
        var copy = _session.Current.Flows.Single(f => f.Id == "a-copy");
        Assert.Equal("A copy", copy.Name);
        Assert.Empty(copy.Hotkeys);
        Assert.NotEmpty(_session.Current.Flows[0].Hotkeys);

        page.UndoCommand.Execute(null);
        Assert.Equal("a b c", Order());
    }

    [Fact]
    public void DuplicateOfSeveralIsOneUndoAndStaysInTheGroup()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        Assert.Equal(2, page.Duplicate(["a", "b"]).Count);
        Assert.Equal("a a-copy b b-copy c", Order());
        Assert.Equal("Audio", _session.Current.Flows.Single(f => f.Id == "b-copy").Tray.Folder);
        Assert.False(_session.Current.Flows.Single(f => f.Id == "b-copy").Tray.Separator);
        page.UndoCommand.Execute(null);
        Assert.Equal("a b c", Order());
    }

    [Fact]
    public void TrayTogglesWriteTheFlowAndUndoInOneStep()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        page.SetShowInTray(["a", "c"], false);
        Assert.All(["a", "c"], id => Assert.False(_session.Current.Flows.Single(f => f.Id == id).Tray.Show));
        Assert.True(page.Items.Single(i => i.Id == "a").NotInTray);
        page.UndoCommand.Execute(null);
        Assert.True(_session.Current.Flows.Single(f => f.Id == "a").Tray.Show);

        page.SetLineAbove(["c"], true);
        Assert.True(_session.Current.Flows.Single(f => f.Id == "c").Tray.Separator);
        Assert.True(page.Items.Single(i => i.Id == "c").LineAbove);
        page.SetLineAbove(["b"], false);
        Assert.False(_session.Current.Flows.Single(f => f.Id == "b").Tray.Separator);
    }
}
