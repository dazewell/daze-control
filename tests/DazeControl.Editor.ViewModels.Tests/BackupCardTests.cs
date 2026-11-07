using DazeControl.Core.Backup;
using DazeControl.Editor.ViewModels.BackupCard;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Settings › Backup and restore (T19.4, D-103): everything runs in a temp folder, never on %APPDATA%.</summary>
public sealed class BackupCardTests : IDisposable
{
    private const string Mine = """
        version: 1
        flows:
          f:
            name: Mine
            nodes:
              - { id: a, step: wait, with: { duration: 1s } }
          only-here:
            name: Only here
            nodes:
              - { id: a, step: wait, with: { duration: 1s } }
        """;

    private const string Theirs = """
        version: 1
        flows:
          f:
            name: Theirs
            nodes:
              - { id: a, step: wait, with: { duration: 2s } }
          fresh:
            name: Fresh
            nodes:
              - { id: a, step: wait, with: { duration: 1s } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _config;
    private readonly string _auto;
    private readonly string _earlier;
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly BackupService _service;
    private readonly BackupCardViewModel _card;

    public BackupCardTests()
    {
        Directory.CreateDirectory(_folder);
        _config = Path.Combine(_folder, "config.yaml");
        _auto = Path.Combine(_folder, "auto");
        _earlier = Path.Combine(_folder, "earlier");
        File.WriteAllText(_config, Mine);
        _session = new ConfigSession(new ConfigStore(_config, _earlier, _time), _time, a => a());
        _session.Load();
        _service = new BackupService(new PhysicalBackupFiles(), _time, "0.0.0-test", "TEST-PC");
        _card = new BackupCardViewModel(_session, _service, new PhysicalBackupFiles(),
            new RestoreTarget(_config, Path.Combine(_folder, "editor.json"), _auto), _earlier, _time, machine: "TEST-PC");
    }

    public void Dispose()
    {
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string MakeBackup(string yaml, string name = "other.dcbackup")
    {
        var path = Path.Combine(_folder, name);
        _service.CreateAt(path, new BackupContents(System.Text.Encoding.UTF8.GetBytes(yaml)), BackupService.Manual);
        return path;
    }

    [Fact]
    public void BackupNowWritesAFileTheServiceCanOpen()
    {
        var target = Path.Combine(_folder, "mine.dcbackup");

        Assert.True(_card.BackupNow(target));

        Assert.Equal(BackupMessageKind.Success, _card.MessageKind);
        Assert.Equal(Mine, System.Text.Encoding.UTF8.GetString(_service.Open(target).Config));
    }

    [Fact]
    public void BackupNowWithoutAConfigSaysSo()
    {
        File.Delete(_config);

        Assert.False(_card.BackupNow(Path.Combine(_folder, "x.dcbackup")));

        Assert.Equal(BackupMessageKind.Error, _card.MessageKind);
        Assert.False(File.Exists(Path.Combine(_folder, "x.dcbackup")));
    }

    [Fact]
    public void PreviewModelCountsAndNamesWhatChanges()
    {
        var dialog = _card.OpenForRestore(MakeBackup(Theirs))!;

        Assert.Contains("Flows: 1 new, 1 changed, 1 only on this PC", dialog.Summary);
        Assert.True(dialog.ReplaceSelected);
        Assert.Contains(dialog.Rows, r => r is { Name: "fresh", State: "New" });
        Assert.Contains(dialog.Rows, r => r is { Name: "only-here", State: "Only on this PC, will be removed" });

        dialog.AddMissingSelected = true;

        Assert.Contains(dialog.Rows, r => r is { Name: "only-here", State: "Only on this PC, kept" });
    }

    [Fact]
    public void ReplaceRestoresMakesASafetyCopyAndReloadsTheSession()
    {
        var dialog = _card.OpenForRestore(MakeBackup(Theirs))!;

        Assert.True(dialog.Apply());

        Assert.Equal(BackupMessageKind.Success, _card.MessageKind);
        Assert.Equal(Theirs, File.ReadAllText(_config));
        Assert.Contains(_session.Current.Flows, f => f.Id == "fresh");
        Assert.DoesNotContain(_session.Current.Flows, f => f.Id == "only-here");
        Assert.Single(_card.Backups);
        Assert.Equal("Before restore", _card.Backups[0].Title);
        Assert.True(_card.CanUndo);
    }

    [Fact]
    public void UndoPutsTheOldConfigBackAndGoesAway()
    {
        _card.OpenForRestore(MakeBackup(Theirs))!.Apply();

        Assert.True(_card.Undo());

        Assert.Equal(Mine, File.ReadAllText(_config));
        Assert.Contains(_session.Current.Flows, f => f.Id == "only-here");
        Assert.False(_card.CanUndo);
        Assert.False(_card.Undo());
    }

    [Fact]
    public void AddMissingKeepsWhatIsOnlyHere()
    {
        var dialog = _card.OpenForRestore(MakeBackup(Theirs))!;
        dialog.AddMissingSelected = true;

        Assert.True(dialog.Apply());

        Assert.Contains(_session.Current.Flows, f => f.Id == "fresh");
        Assert.Contains(_session.Current.Flows, f => f.Id == "only-here");
        Assert.Equal("Mine", _session.Current.Flows.Single(f => f.Id == "f").Name);
    }

    [Fact]
    public void AnOpenFlowDraftRefusesTheRestoreAndUndo()
    {
        var path = MakeBackup(Theirs);
        _session.UpdateDraft("f", f => f with { Name = "Edited" });

        Assert.Null(_card.OpenForRestore(path));

        Assert.Equal("Save or discard the open flow first.", _card.Message);
        Assert.Equal(BackupMessageKind.Error, _card.MessageKind);
        Assert.Equal(Mine, File.ReadAllText(_config));
    }

    [Fact]
    public void ADraftOpenedAfterThePreviewStillRefusesRestore()
    {
        var dialog = _card.OpenForRestore(MakeBackup(Theirs))!;
        _session.UpdateDraft("f", f => f with { Name = "Edited" });

        Assert.False(dialog.Apply());

        Assert.Equal(Mine, File.ReadAllText(_config));
    }

    [Fact]
    public void AnUnreadableBackupConfigDisablesRestore()
    {
        var dialog = _card.OpenForRestore(MakeBackup("flows: [unclosed"))!;

        Assert.True(dialog.IsUnreadable);
        Assert.False(dialog.CanRestore);
        Assert.False(dialog.Apply());
        Assert.Equal(Mine, File.ReadAllText(_config));
    }

    [Fact]
    public void ANonBackupFileShowsAPlainMessage()
    {
        var path = Path.Combine(_folder, "not.dcbackup");
        File.WriteAllText(path, "hello");

        Assert.Null(_card.OpenForRestore(path));

        Assert.Equal("This isn't a Daze Control backup.", _card.Message);
    }

    [Fact]
    public void EarlierSavesListAndRestoreConfigOnly()
    {
        Directory.CreateDirectory(_earlier);
        File.WriteAllText(Path.Combine(_earlier, "config-20261106-213000.yaml"), Theirs);
        File.WriteAllText(Path.Combine(_earlier, "notes.txt"), "not ours");
        _card.Refresh();

        var row = Assert.Single(_card.EarlierSaves);
        var dialog = _card.OpenForRestore(row.Path, earlier: true)!;

        Assert.True(dialog.Apply());
        Assert.Equal(Theirs, File.ReadAllText(_config));
    }

    [Fact]
    public void DeleteRemovesTheFileAndTheRow()
    {
        _card.OpenForRestore(MakeBackup(Theirs))!.Apply();
        var row = Assert.Single(_card.Backups);

        _card.Delete(row);

        Assert.Empty(_card.Backups);
        Assert.False(File.Exists(row.Path));
    }
}
