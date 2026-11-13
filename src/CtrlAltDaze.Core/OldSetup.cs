namespace CtrlAltDaze.Core;

/// <summary>
/// Brings the user's setup over from the old name (T29.2, D-152): the engine, editor and CLI call
/// <see cref="CopyFolders()"/> first. Copies, never moves, so the old app keeps working until it is removed.
/// </summary>
public static class OldSetup
{
    private const string ConfigFile = "config.yaml";

    /// <summary>A line for the log, or null when there was nothing to do.</summary>
    public static string? CopyFolders() =>
        CopyFolders(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Only while the new config.yaml is absent and the old one exists. Files already in the new folders stay; the
    /// config is copied last, so a copy that stopped half-way is finished on the next start. Never throws (fail-soft).
    /// </summary>
    internal static string? CopyFolders(string roaming, string local)
    {
        var oldRoaming = Path.Combine(roaming, OldName.FolderName);
        var newRoaming = Path.Combine(roaming, AppInfo.FolderName);
        var oldConfig = Path.Combine(oldRoaming, ConfigFile);
        var newConfig = Path.Combine(newRoaming, ConfigFile);
        if (File.Exists(newConfig) || !File.Exists(oldConfig))
            return null;
        try
        {
            var files = CopyTree(Path.Combine(local, OldName.FolderName), Path.Combine(local, AppInfo.FolderName), skip: null)
                + CopyTree(oldRoaming, newRoaming, skip: oldConfig);
            Directory.CreateDirectory(newRoaming);
            // Through a temporary file: a half-written config.yaml would stop the copy from ever being tried again.
            var partial = newConfig + ".copying";
            File.Copy(oldConfig, partial, overwrite: true);
            File.Move(partial, newConfig);
            return $"setup: copied {files + 1} files from the {OldName.FolderName} folders";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"setup: copying from the {OldName.FolderName} folders stopped: {e.Message}";
        }
    }

    private static int CopyTree(string from, string to, string? skip)
    {
        if (!Directory.Exists(from))
            return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (skip is not null && string.Equals(file, skip, StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (File.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            count++;
        }
        return count;
    }
}
