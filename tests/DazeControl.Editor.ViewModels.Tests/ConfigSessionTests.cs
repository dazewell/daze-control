using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class ConfigSessionTests : IDisposable
{
    internal const string Yaml = """
        version: 1
        flows:
          connect-technics:
            name: Connect Technics
            hotkeys: [Alt+Z]
            nodes:
              - { id: wait, step: wait, with: { duration: 1s } }
          tv:
            name: TV
            nodes:
              - { id: wait, step: wait, with: { duration: 1s } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;

    public ConfigSessionTests()
    {
        Directory.CreateDirectory(_folder);
        _session = new ConfigSession(new ConfigStore(ConfigPath, BackupFolder, _time), _time, action => action());
    }

    private string ConfigPath => Path.Combine(_folder, "config.yaml");

    private string BackupFolder => Path.Combine(_folder, "backups");

    public void Dispose()
    {
        _session.Dispose();
        _time.Dispose();
        foreach (var file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_folder, recursive: true);
    }

    private static Configuration Rename(Configuration c, string name) =>
        c with { Flows = [.. c.Flows.Select(f => f with { Name = name })] };

    [Fact]
    public void MissingFileIsEmptyAndTheFirstEditCreatesIt()
    {
        _session.Load();

        Assert.True(_session.CanEdit);
        Assert.Empty(_session.Current.Flows);

        _session.Update(c => c with { Flows = [new Flow("a", "A", [])] });
        _session.Flush();

        Assert.Contains("name: A", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(BackupFolder)); // nothing to back up
    }

    [Fact]
    public async Task EditsAreSavedOnceAfterTheLastOneSettles()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();

        _session.Update(c => Rename(c, "One"));
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _session.Update(c => Rename(c, "Two"));
        _time.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(Yaml, File.ReadAllText(ConfigPath));

        _time.Advance(TimeSpan.FromMilliseconds(100));

        var saved = File.ReadAllText(ConfigPath);
        Assert.Contains("name: Two", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("One", saved, StringComparison.Ordinal);
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void TheFirstSaveOfASessionBacksUpTheFile()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();

        _session.Update(c => Rename(c, "One"));
        _session.Flush();
        _session.Update(c => Rename(c, "Two"));
        _session.Flush();

        var backup = Assert.Single(Directory.GetFiles(BackupFolder));
        Assert.Equal(Yaml, File.ReadAllText(backup));
    }

    [Fact]
    public void BackupsKeepOnlyTheNewest()
    {
        Directory.CreateDirectory(BackupFolder);
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(BackupFolder, $"config-2020010{i % 10}-0000{i:00}.yaml"), "old");
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();

        _session.Update(c => Rename(c, "One"));
        _session.Flush();

        Assert.Equal(10, Directory.GetFiles(BackupFolder).Length);
    }

    [Fact]
    public void AnUnreadableFileIsNeverOverwritten()
    {
        const string broken = "version: 1\nflows: [\n";
        File.WriteAllText(ConfigPath, broken);

        _session.Load();
        _session.Update(c => c with { Flows = [new Flow("a", "A", [])] });
        _session.Flush();

        Assert.False(_session.CanEdit);
        Assert.True(_session.HasLoadError);
        Assert.Equal(broken, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void AnOutsideChangeWithNothingUnsavedIsTakenAsIs()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();

        File.WriteAllText(ConfigPath, Yaml.Replace("name: TV", "name: Telly", StringComparison.Ordinal));
        _session.CheckOutsideChange();

        Assert.Equal(OutsideChange.Reloaded, _session.OutsideChange);
        Assert.Contains(_session.Current.Flows, f => f.Name == "Telly");
    }

    [Fact]
    public void OurOwnSaveIsNotAnOutsideChange()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();
        _session.Update(c => Rename(c, "One"));
        _session.Flush();

        _session.CheckOutsideChange();

        Assert.Equal(OutsideChange.None, _session.OutsideChange);
    }

    [Fact]
    public void AnOutsideChangeOverUnsavedEditsWaitsForTheUser()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();
        _session.Update(c => Rename(c, "Mine"));
        var theirs = Yaml.Replace("name: TV", "name: Theirs", StringComparison.Ordinal);
        File.WriteAllText(ConfigPath, theirs);

        _session.CheckOutsideChange();
        _session.Flush();

        Assert.Equal(OutsideChange.Conflict, _session.OutsideChange);
        Assert.Equal(theirs, File.ReadAllText(ConfigPath));

        _session.KeepMineCommand.Execute(null);

        Assert.Equal(OutsideChange.None, _session.OutsideChange);
        Assert.Contains("name: Mine", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadDropsTheUnsavedEdits()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();
        _session.Update(c => Rename(c, "Mine"));
        File.WriteAllText(ConfigPath, Yaml.Replace("name: TV", "name: Theirs", StringComparison.Ordinal));
        _session.CheckOutsideChange();

        _session.ReloadCommand.Execute(null);
        _session.Flush();

        Assert.Equal(OutsideChange.None, _session.OutsideChange);
        Assert.Contains(_session.Current.Flows, f => f.Name == "Theirs");
        Assert.DoesNotContain("Mine", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedSaveIsShownAndTheNextEditTriesAgain()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();
        File.SetAttributes(ConfigPath, FileAttributes.ReadOnly);

        _session.Update(c => Rename(c, "One"));
        _session.Flush();

        Assert.True(_session.HasSaveError);
        Assert.Equal(Yaml, File.ReadAllText(ConfigPath));

        File.SetAttributes(ConfigPath, FileAttributes.Normal);
        _session.Update(c => Rename(c, "Two"));
        _session.Flush();

        Assert.False(_session.HasSaveError);
        Assert.Contains("name: Two", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithMappingErrorsIsReadOnly()
    {
        var yaml = Yaml.Replace("version: 1", "version: 1\nsettings: { popup: { seconds: 99 } }", StringComparison.Ordinal);
        File.WriteAllText(ConfigPath, yaml);

        _session.Load();
        _session.Update(c => Rename(c, "One"));
        _session.Flush();

        Assert.False(_session.CanEdit);
        Assert.Contains("settings.popup.seconds", _session.LoadError, StringComparison.Ordinal);
        Assert.Contains(_session.Problems, p => p.Path == "settings.popup.seconds");
        Assert.Equal(yaml, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void AnOutsideWriteTheWatcherHasntReportedYetIsNotOverwritten()
    {
        File.WriteAllText(ConfigPath, Yaml);
        _session.Load();
        _session.Update(c => Rename(c, "Mine"));
        var theirs = Yaml.Replace("name: TV", "name: Theirs", StringComparison.Ordinal);
        File.WriteAllText(ConfigPath, theirs);

        _session.Flush();

        Assert.Equal(OutsideChange.Conflict, _session.OutsideChange);
        Assert.Equal(theirs, File.ReadAllText(ConfigPath));
        Assert.True(_session.HasUnsavedChanges); // the window asks before closing
    }
}
