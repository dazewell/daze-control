namespace DazeControl.Runtime.Triggers;

/// <summary>What happened to a file or folder directly inside a watched folder.</summary>
public enum FileChange
{
    Created,
    Changed,
    Deleted,
}

/// <summary>
/// The operating system's folder notifications behind <see cref="FileEventSource"/> (T26.5): one seam so tests drive the
/// source with a fake and never watch a real folder. Callbacks come from any thread.
/// </summary>
public interface IFolderWatcher
{
    /// <summary>Whether <paramref name="folder"/> is there now. Never throws; may be slow on a dead share, so it is only asked off the message thread.</summary>
    bool Exists(string folder);

    /// <summary>
    /// Starts watching <paramref name="folder"/> (not its subfolders). <paramref name="nameFilter"/> limits it to one name; with
    /// <paramref name="directoriesOnly"/> only folders being made, renamed or removed are reported (the folder-gone guard).
    /// A rename is reported as the old name deleted and the new one created. <paramref name="broken"/> says the watch stopped
    /// working (the folder went away). Throws when the folder can't be watched; disposing stops it.
    /// </summary>
    IDisposable Watch(string folder, string? nameFilter, bool directoriesOnly, Action<FileChange, string> changed, Action broken);
}

/// <summary>
/// <see cref="FileSystemWatcher"/> (ReadDirectoryChangesW with a completion port): nothing is read until a change happens, no
/// polling, no reflection. One watcher per call, created only while a flow subscribes (D-009).
/// </summary>
public sealed class SystemFolderWatcher : IFolderWatcher
{
    public bool Exists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public IDisposable Watch(string folder, string? nameFilter, bool directoriesOnly, Action<FileChange, string> changed, Action broken)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = directoriesOnly ? NotifyFilters.DirectoryName : NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 32 * 1024, // a burst of 500 files; an overflow only loses events, it doesn't break the watch
        };
        try
        {
            if (nameFilter is not null)
                watcher.Filter = nameFilter;
            watcher.Created += (_, e) => changed(FileChange.Created, e.Name ?? "");
            watcher.Changed += (_, e) => changed(FileChange.Changed, e.Name ?? "");
            watcher.Deleted += (_, e) => changed(FileChange.Deleted, e.Name ?? "");
            watcher.Renamed += (_, e) =>
            {
                changed(FileChange.Deleted, e.OldName ?? "");
                changed(FileChange.Created, e.Name ?? "");
            };
            watcher.Error += (_, e) =>
            {
                if (e.GetException() is not InternalBufferOverflowException)
                    broken();
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }
}
