namespace DazeControl.Core.Backup;

/// <summary>What a backup holds (D-103). Config is the file's bytes, never re-serialised.</summary>
public sealed record BackupContents(byte[] Config, byte[]? EditorJson = null, byte[]? History = null);

/// <summary>One payload entry listed in the manifest.</summary>
public sealed record BackupEntryInfo(string Name, long Bytes, string Sha256);

/// <summary>The manifest.yaml of a backup. Counts for the preview are recomputed on read, not stored.</summary>
public sealed record BackupManifest(
    int Format, string App, int ConfigVersion, DateTimeOffset Created, string Machine, string Reason,
    IReadOnlyList<BackupEntryInfo> Entries);

/// <summary>A verified backup: every entry was hashed and matches the manifest.</summary>
public sealed class BackupArchive
{
    private readonly Dictionary<string, byte[]> _data;
    internal BackupArchive(BackupManifest manifest, Dictionary<string, byte[]> data) { Manifest = manifest; _data = data; }

    public BackupManifest Manifest { get; }
    public byte[] Config => _data[BackupFormat.ConfigName];
    public byte[]? EditorJson => _data.GetValueOrDefault(BackupFormat.EditorName);
    public byte[]? History => _data.GetValueOrDefault(BackupFormat.HistoryName);
}

public enum BackupFailure { NotABackup, Damaged, TooNew }

/// <summary>Why a file could not be opened; Kind picks the message (DESIGN §10).</summary>
public sealed class BackupException(BackupFailure kind, string message) : Exception(message)
{
    public BackupFailure Kind { get; } = kind;
}

/// <summary>Result of writing a backup. Warnings are secret-looking values (D-014); the file is still written.</summary>
public sealed record BackupResult(string Path, BackupManifest Manifest, IReadOnlyList<string> Warnings);

/// <summary>A backup file found by name in a folder; Stamp and Counter come from the file name.</summary>
public sealed record BackupFile(string Path, string Stamp, int Counter);

/// <summary>File system seam so tests never touch %APPDATA% / %LOCALAPPDATA%.</summary>
public interface IBackupFiles
{
    bool Exists(string path);
    byte[] ReadAll(string path);
    /// <summary>Writes a new file (temp + move, creates the folder); returns false, touching nothing, when the path already exists, even if it appeared a moment ago.</summary>
    bool TryWriteNew(string path, byte[] data);
    void Delete(string path);
    /// <summary>The file's bytes, or null when it does not exist.</summary>
    byte[]? TryReadAll(string path);
    /// <summary>Replaces (or creates) the file atomically: temp file in the same folder + move, so a failure leaves the old file intact and readers see old or new, never half.</summary>
    void ReplaceAtomic(string path, byte[] data);
    /// <summary>Full paths of the files directly in the folder; empty when it is missing.</summary>
    IReadOnlyList<string> ListFiles(string folder);
}

public sealed class PhysicalBackupFiles : IBackupFiles
{
    public bool Exists(string path) => File.Exists(path);
    public byte[] ReadAll(string path) => File.ReadAllBytes(path);
    public void Delete(string path) => File.Delete(path);

    public bool TryWriteNew(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path)) { return false; }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // Only "not there" is null; an unreadable file must not look like a missing one (a restore would skip the safety copy).
    public byte[]? TryReadAll(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void ReplaceAtomic(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public IReadOnlyList<string> ListFiles(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
}
