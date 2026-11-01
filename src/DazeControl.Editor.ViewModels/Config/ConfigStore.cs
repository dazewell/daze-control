using System.Globalization;
using System.Text;
using DazeControl.Core;
using DazeControl.Core.Model;
using DazeControl.Core.Yaml;

namespace DazeControl.Editor.ViewModels.Config;

/// <summary>
/// config.yaml on disk for the editor (D-004): reads it, writes it atomically (temp file + rename, which the
/// engine's watcher sees as one change), copies the file into <see cref="BackupDirectory"/> before the first
/// write of a session, and remembers the text it last read or wrote so a change by someone else shows.
/// </summary>
public sealed class ConfigStore
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly int _keepBackups;
    private readonly TimeProvider _time;
    private string? _known;
    private bool _backedUp;

    public ConfigStore(string path, string backupDirectory, TimeProvider? time = null, int keepBackups = 10)
    {
        Path = System.IO.Path.GetFullPath(path);
        BackupDirectory = backupDirectory;
        _time = time ?? TimeProvider.System;
        _keepBackups = keepBackups;
    }

    public static string DefaultPath { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName, "config.yaml");

    public static string DefaultBackupDirectory { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName, "backups");

    public string Path { get; }

    public string BackupDirectory { get; }

    /// <summary>A missing file is an empty configuration, as in the engine.</summary>
    /// <exception cref="IOException">The file exists but can't be read.</exception>
    public ConfigLoad Load()
    {
        var text = ReadOrNull();
        _known = text;
        return text is null ? new ConfigLoad(Configuration.Empty, []) : ConfigYaml.Load(text);
    }

    /// <summary>True when the file's text is no longer what this store last read or wrote.</summary>
    public bool ChangedOnDisk()
    {
        try
        {
            return ReadOrNull() != _known;
        }
        catch (IOException)
        {
            return false; // mid-write by someone else: the watcher fires again when it's done
        }
        catch (UnauthorizedAccessException)
        {
            return true; // replaced by a file we can't read: a change; Load reports it
        }
    }

    /// <summary>Writes <paramref name="config"/> unless the file already holds exactly that.</summary>
    /// <exception cref="IOException">Or <see cref="UnauthorizedAccessException"/>: nothing was replaced.</exception>
    public void Save(Configuration config)
    {
        var text = ConfigYaml.Save(config);
        if (text == _known)
            return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (!_backedUp)
        {
            BackUp();
            _backedUp = true;
        }
        var temp = Path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Utf8.GetBytes(text));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, Path, overwrite: true); // MoveFileEx(REPLACE_EXISTING): readers see old or new, never half
        _known = text;
    }

    private string? ReadOrNull()
    {
        try
        {
            return File.ReadAllText(Path, Utf8);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private void BackUp()
    {
        if (!File.Exists(Path))
            return;
        Directory.CreateDirectory(BackupDirectory);
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        File.Copy(Path, System.IO.Path.Combine(BackupDirectory, $"config-{stamp}.yaml"), overwrite: true);
        foreach (var old in Directory.GetFiles(BackupDirectory, "config-*.yaml").Order(StringComparer.Ordinal).SkipLast(_keepBackups))
            File.Delete(old);
    }
}
