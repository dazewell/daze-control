using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>editor.json (D-068): view state read once, written atomically 300 ms after the last change.</summary>
public sealed class EditorSettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly List<string> _log = [];

    public EditorSettingsStoreTests() => Directory.CreateDirectory(_folder);

    private string SettingsPath => Path.Combine(_folder, "editor.json");

    private EditorSettingsStore Open() => new(SettingsPath, _time, _log.Add);

    public void Dispose()
    {
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void MissingFileGivesDefaultsWithoutALogLineOrAFile()
    {
        using var store = Open();

        Assert.True(store.Settings.NavPaneOpen);
        Assert.Empty(store.Settings.Flows);
        Assert.Empty(_log);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void MissingFolderGivesDefaultsAndIsCreatedOnWrite()
    {
        var path = Path.Combine(_folder, "sub", "editor.json");
        using (var store = new EditorSettingsStore(path, _time, _log.Add))
        {
            Assert.True(store.Settings.NavPaneOpen);
            store.Update(s => s.NavPaneOpen = false);
        }

        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{\"navPaneOpen\": \"yes\"}")]
    public void CorruptFileGivesDefaultsAndALogLine(string text)
    {
        File.WriteAllText(SettingsPath, text);

        using var store = Open();

        Assert.True(store.Settings.NavPaneOpen);
        Assert.Empty(store.Settings.Flows);
        Assert.Single(_log);
        Assert.Contains("editor.json", _log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ChangesRoundTripThroughTheFile()
    {
        using (var store = Open())
        {
            store.Update(s =>
            {
                s.NavPaneOpen = false;
                s.Flows["connect-technics"] = new FlowViewSettings();
            });
        }

        using var again = Open();
        Assert.False(again.Settings.NavPaneOpen);
        Assert.True(again.Settings.Flows.ContainsKey("connect-technics"));
        Assert.Empty(_log);
    }

    [Fact]
    public void UnknownKeysFromANewerEditorSurviveAWrite()
    {
        File.WriteAllText(SettingsPath, """{ "navPaneOpen": true, "later": { "x": 1 } }""");
        using (var store = Open())
            store.Update(s => s.NavPaneOpen = false);

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"later\"", text, StringComparison.Ordinal);
        Assert.Contains("\"navPaneOpen\": false", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlowsViewChoicesFollowARename()
    {
        using (var store = Open())
        {
            store.Update(s => s.Flows["tv"] = new FlowViewSettings());
            store.RenameFlow("tv", "living-room-tv");
            store.RenameFlow("missing", "other"); // nothing to move: no entry made
        }

        using var again = Open();
        Assert.Equal(["living-room-tv"], again.Settings.Flows.Keys);
    }

    [Fact]
    public async Task WritesOnce300MsAfterTheLastChange()
    {
        using var store = Open();

        store.Update(s => s.NavPaneOpen = false);
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.False(File.Exists(SettingsPath));

        store.Update(s => s.NavPaneOpen = true);
        store.Update(s => s.NavPaneOpen = false);
        _time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.False(File.Exists(SettingsPath)); // each change restarts the wait

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(File.Exists(SettingsPath));
        Assert.Contains("\"navPaneOpen\": false", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public async Task NothingChangedWritesNothing()
    {
        using (var store = Open())
        {
            await _time.WaitForTimerAsync();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void DisposeWritesAPendingChange()
    {
        using (var store = Open())
            store.Update(s => s.NavPaneOpen = false);

        Assert.Contains("\"navPaneOpen\": false", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedWriteIsLoggedAndDoesNotThrow()
    {
        using var store = Open();
        using (new FileStream(SettingsPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            store.Update(s => s.NavPaneOpen = false);
            store.Flush();
        }

        Assert.Single(_log);
        store.Flush(); // still pending: goes once the file is free
        Assert.Contains("\"navPaneOpen\": false", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TheShellOpensThePaneAsLeftAndRemembersAToggle()
    {
        File.WriteAllText(SettingsPath, """{ "navPaneOpen": false }""");
        using var store = Open();
        using var session = new ConfigSession(new ConfigStore(Path.Combine(_folder, "config.yaml"), Path.Combine(_folder, "backups"), _time), _time, a => a());
        session.Load();
        using (var shell = new ShellViewModel(session, new FakeEngine(), a => a(), _folder, editorSettings: store))
        {
            Assert.False(shell.IsNavPaneOpen);
            shell.IsNavPaneOpen = true;
        }

        store.Flush();
        using var again = Open();
        Assert.True(again.Settings.NavPaneOpen);
    }

    [Fact]
    public void WithoutAStoreThePaneStartsOpen()
    {
        using var session = new ConfigSession(new ConfigStore(Path.Combine(_folder, "config.yaml"), Path.Combine(_folder, "backups"), _time), _time, a => a());
        session.Load();
        using var shell = new ShellViewModel(session, new FakeEngine(), a => a(), _folder);

        Assert.True(shell.IsNavPaneOpen);
        shell.IsNavPaneOpen = false; // nothing to write to: no throw
    }
}
