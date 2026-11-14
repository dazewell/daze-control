using System.Text;
using CtrlAltDaze.Core.Backup;

namespace CtrlAltDaze.Core.Tests;

public class BackupCommandsTests
{
    private static readonly BackupPaths Paths = new(@"X:\cfg\config.yaml", @"X:\cfg\editor.json", @"X:\cfg\history.jsonl", @"X:\cfg\backups\auto");

    private const string Mine = "flows:\n  keep: { name: Keep, nodes: [{ id: a, step: notify, with: { text: x } }] }\n";
    private const string Theirs = "flows:\n  fresh: { name: Fresh, nodes: [{ id: a, step: notify, with: { text: z } }] }\n";

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 6, 21, 30, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Run
    {
        public BackupTests.MemFiles Files { get; } = new();
        public StringWriter Out { get; } = new();
        public StringWriter Err { get; } = new();
        public bool Interactive { get; set; }
        public bool EditorOpen { get; set; }
        public string? Answer { get; set; }
        public BackupCommands Cmd => new(new BackupService(Files, new FixedTime(), "0.46.0", "DAZE-PC"), Files, Paths, Out, Err,
            Interactive, () => EditorOpen, () => Answer);
        public Run WithConfig(string text = Mine) { Files.Files[Paths.Config] = Encoding.UTF8.GetBytes(text); return this; }
        public string Backup(string text = Theirs)
        {
            var other = new Run().WithConfig(text);
            var path = new BackupService(Files, new FixedTime(), "0.46.0", "OTHER-PC")
                .CreateAt(@"X:\in\theirs.dcbackup", new BackupContents(Encoding.UTF8.GetBytes(text)), BackupService.Manual).Path;
            _ = other;
            return path;
        }
        public string Config => Encoding.UTF8.GetString(Files.Files[Paths.Config]);
    }

    [Fact]
    public void BackupWithoutPathWritesToTheAutoFolder()
    {
        var r = new Run().WithConfig();
        Assert.Equal(0, r.Cmd.Backup(null, false, false));
        Assert.Contains(r.Files.Files.Keys, k => k.StartsWith(@"X:\cfg\backups\auto\CtrlAltDaze-backup-", StringComparison.Ordinal));
        Assert.Contains("Backed up to", r.Out.ToString());
    }

    [Fact]
    public void BackupToAFileKeepsThePathAndItsExtension()
    {
        var r = new Run().WithConfig();
        Assert.Equal(0, r.Cmd.Backup(@"X:\out\mine.dcbackup", false, false));
        Assert.True(r.Files.Files.ContainsKey(@"X:\out\mine.dcbackup"));
    }

    [Fact]
    public void BackupWithoutAConfigFails()
    {
        var r = new Run();
        Assert.Equal(1, r.Cmd.Backup(null, false, false));
        Assert.Contains("no config.yaml", r.Err.ToString());
    }

    [Fact]
    public void BackupIncludesTheEditorLayoutOnlyWhenAsked()
    {
        var r = new Run().WithConfig();
        r.Files.Files[Paths.EditorJson] = Encoding.UTF8.GetBytes("{}");
        r.Cmd.Backup(@"X:\o\a.dcbackup", false, false);
        r.Cmd.Backup(@"X:\o\b.dcbackup", true, false);
        var svc = new BackupService(r.Files, new FixedTime(), "0.46.0", "m");
        Assert.Null(svc.Open(@"X:\o\a.dcbackup").EditorJson);
        Assert.NotNull(svc.Open(@"X:\o\b.dcbackup").EditorJson);
    }

    [Fact]
    public void BackupDiskErrorIsAPlainSentence()
    {
        var r = new Run().WithConfig();
        r.Files.FailNewFiles = true;
        Assert.Equal(1, r.Cmd.Backup(@"X:\o\a.dcbackup", false, false));
        Assert.Contains(@"Couldn't save the backup to X:\o\a.dcbackup: disk full", r.Err.ToString());
    }

    [Fact]
    public void DryRunShowsThePreviewAndChangesNothing()
    {
        var r = new Run().WithConfig();
        var file = r.Backup();
        Assert.Equal(0, r.Cmd.Restore(file, RestoreMode.Replace, yes: false, dryRun: true, editorLayout: false));
        var text = r.Out.ToString();
        Assert.Contains("made on OTHER-PC", text);
        Assert.Contains("Flows: 1 new, 1 only on this PC", text);
        Assert.Contains("Dry run", text);
        Assert.Equal(Mine, r.Config);
    }

    [Fact]
    public void WithoutYesANonInteractiveRestoreIsRefused()
    {
        var r = new Run().WithConfig();
        var file = r.Backup();
        Assert.Equal(2, r.Cmd.Restore(file, RestoreMode.Replace, false, false, false));
        Assert.Contains("--yes", r.Err.ToString());
        Assert.Equal(Mine, r.Config);
        Assert.DoesNotContain(r.Files.Files.Keys, k => k.Contains("backups", StringComparison.Ordinal));
    }

    [Fact]
    public void YesReplacesAndMakesASafetyCopy()
    {
        var r = new Run().WithConfig();
        var file = r.Backup();
        Assert.Equal(0, r.Cmd.Restore(file, RestoreMode.Replace, true, false, false));
        Assert.Equal(Theirs, r.Config);
        Assert.Contains(r.Files.Files.Keys, k => k.StartsWith(@"X:\cfg\backups\auto\", StringComparison.Ordinal));
        Assert.Contains("To undo", r.Out.ToString());
    }

    [Fact]
    public void AddMissingKeepsWhatIsHere()
    {
        var r = new Run().WithConfig();
        var file = r.Backup();
        Assert.Equal(0, r.Cmd.Restore(file, RestoreMode.AddMissing, true, false, false));
        Assert.Contains("keep", r.Config);
        Assert.Contains("fresh", r.Config);
    }

    [Theory]
    [InlineData("y", 0, true)]
    [InlineData("n", 2, false)]
    [InlineData("", 2, false)]
    public void AnInteractiveRestoreAsks(string answer, int exit, bool restored)
    {
        var r = new Run { Interactive = true, Answer = answer }.WithConfig();
        var file = r.Backup();
        Assert.Equal(exit, r.Cmd.Restore(file, RestoreMode.Replace, false, false, false));
        Assert.Contains("[y/N]", r.Out.ToString());
        Assert.Equal(restored, r.Config == Theirs);
    }

    [Fact]
    public void AnOpenEditorRefusesTheRestoreButNotThePreview()
    {
        var r = new Run { EditorOpen = true }.WithConfig();
        var file = r.Backup();
        Assert.Equal(0, r.Cmd.Restore(file, RestoreMode.Replace, true, true, false));
        Assert.Equal(2, r.Cmd.Restore(file, RestoreMode.Replace, true, false, false));
        Assert.Contains("The editor is open", r.Err.ToString());
        Assert.Equal(Mine, r.Config);
    }

    [Fact]
    public void NotABackupIsAPlainSentence()
    {
        var r = new Run().WithConfig();
        r.Files.Files[@"X:\in\junk.dcbackup"] = Encoding.UTF8.GetBytes("hello");
        Assert.Equal(1, r.Cmd.Restore(@"X:\in\junk.dcbackup", RestoreMode.Replace, true, false, false));
        Assert.Contains("This isn't a Ctrl Alt Daze backup.", r.Err.ToString());
        Assert.Equal(Mine, r.Config);
    }

    [Fact]
    public void ABackupWhoseConfigCannotBeReadIsNotRestored()
    {
        var r = new Run().WithConfig();
        var file = r.Backup("flows: [unclosed");
        Assert.Equal(1, r.Cmd.Restore(file, RestoreMode.Replace, true, false, false));
        Assert.Contains("can't be read", r.Err.ToString());
        Assert.Equal(Mine, r.Config);
    }
}
