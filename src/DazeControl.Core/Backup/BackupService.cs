using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DazeControl.Core.Backup;

/// <summary>
/// Create, open and prune .dcbackup files (D-103). Pure: files go through <see cref="IBackupFiles"/>, time through
/// <see cref="TimeProvider"/>. Runs in the editor, `dctl` and the updater, never resident in the engine (D-009).
/// </summary>
public sealed partial class BackupService(IBackupFiles files, TimeProvider time, string appVersion, string machine)
{
    public const string Manual = "manual";
    public const string AutoSave = "auto-save";
    public const string BeforeUpdate = "before-update";
    public const string BeforeRestore = "before-restore";
    public const string Cli = "cli";

    /// <summary>Writes a new, sortable file (DazeControl-backup-yyyy-MM-dd-HHmm.dcbackup) into the folder; never overwrites (adds -2, -3…).</summary>
    public BackupResult CreateIn(string folder, BackupContents contents, string reason)
    {
        var now = time.GetLocalNow();
        var stem = Path.Combine(folder, "DazeControl-backup-" + now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture));
        return Write(stem, BackupFormat.Extension, contents, reason, now);
    }

    /// <summary>Writes to the path the user picked; a taken name gets a counter instead of being overwritten.</summary>
    public BackupResult CreateAt(string path, BackupContents contents, string reason)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) ext = BackupFormat.Extension;
        else path = path[..^ext.Length];
        return Write(path, ext, contents, reason, time.GetLocalNow());
    }

    private BackupResult Write(string stem, string ext, BackupContents contents, string reason, DateTimeOffset now)
    {
        var bytes = BackupFormat.Write(contents, now, appVersion, machine, reason, out var manifest);
        var path = stem + ext;
        for (var n = 2; !files.TryWriteNew(path, bytes); n++) path = $"{stem}-{n}{ext}";
        return new BackupResult(path, manifest, SecretScan.Find(Encoding.UTF8.GetString(contents.Config)));
    }

    /// <summary>Reads and verifies a backup; throws <see cref="BackupException"/> (Kind says why).</summary>
    public BackupArchive Open(string path) => BackupFormat.Open(files.ReadAll(path));

    /// <summary>The backups in a folder by file name, newest first. Files that do not match the naming pattern are not ours and are ignored.</summary>
    public IReadOnlyList<BackupFile> List(string folder) =>
        files.ListFiles(folder)
            .Select(p => (Path: p, M: NameRx().Match(System.IO.Path.GetFileName(p))))
            .Where(x => x.M.Success)
            .Select(x => (x.Path, Stamp: x.M.Groups[1].Value, Counter: ParseCounter(x.M.Groups[2])))
            .Where(x => x.Counter > 0)
            .Select(x => new BackupFile(x.Path, x.Stamp, x.Counter))
            .OrderByDescending(f => f.Stamp, StringComparer.Ordinal).ThenByDescending(f => f.Counter)
            .ToList();

    /// <summary>Keeps the newest <paramref name="keep"/> (at least 1) backups of the folder and deletes the rest; returns the deleted paths. Name-based, no zip is opened.</summary>
    public IReadOnlyList<string> Prune(string folder, int keep)
    {
        var old = List(folder).Skip(Math.Max(keep, 1)).Select(f => f.Path).ToList();
        foreach (var p in old) files.Delete(p);
        return old;
    }

    // 0 = out of range: a foreign or damaged name is skipped, never thrown on.
    private static int ParseCounter(Group g) =>
        !g.Success ? 1 : int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    [GeneratedRegex(@"^DazeControl-backup-(\d{4}-\d{2}-\d{2}-\d{4})(?:-(\d+))?\.dcbackup$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRx();
}
