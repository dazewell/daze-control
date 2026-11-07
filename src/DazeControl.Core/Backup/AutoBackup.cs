using System.Globalization;

namespace DazeControl.Core.Backup;

/// <summary>Automatic backups (T19.5, D-103/D-104): on by default, newest <see cref="Keep"/> kept.</summary>
public sealed record AutoBackupSettings(bool Enabled = true, int Keep = AutoBackupSettings.DefaultKeep)
{
    public const int DefaultKeep = 10;
    public const int MinKeep = 3;
    public const int MaxKeep = 50;

    /// <summary>Two lines, <c>enabled=true</c> and <c>keep=10</c>. Anything unreadable falls back to the defaults, never throws.</summary>
    public static AutoBackupSettings Parse(string? text)
    {
        var enabled = true;
        var keep = DefaultKeep;
        foreach (var line in (text ?? "").Split('\n'))
        {
            var i = line.IndexOf('=', StringComparison.Ordinal);
            if (i < 0) continue;
            var key = line[..i].Trim();
            var value = line[(i + 1)..].Trim();
            if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var b)) enabled = b;
            else if (key.Equals("keep", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) keep = n;
        }
        return new(enabled, Math.Clamp(keep, MinKeep, MaxKeep));
    }

    public string ToText() => string.Create(CultureInfo.InvariantCulture, $"enabled={(Enabled ? "true" : "false")}\nkeep={Math.Clamp(Keep, MinKeep, MaxKeep)}\n");
}

public enum AutoBackupOutcome { Created, Disabled, TooSoon, NoConfig, Failed }

/// <summary>
/// Takes the automatic backups at events that already happen (before an update, after a save): no timer, no watcher.
/// Fail-soft by contract: <see cref="Run"/> never throws (a failed backup must not break the update or a save).
/// Pure over <see cref="IBackupFiles"/> and <see cref="TimeProvider"/>, so tests never touch the real folders.
/// </summary>
public sealed class AutoBackup(BackupService service, IBackupFiles files, TimeProvider time, string configPath, string autoFolder, Action<string>? log = null)
{
    public static readonly TimeSpan Gate = TimeSpan.FromHours(24);

    /// <summary>The settings file sits next to the automatic folder (per PC, like editor.json; it is not in a backup).</summary>
    public static string SettingsPathFor(string autoFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(autoFolder).TrimEnd('\\', '/')) ?? ".", "auto-backup.txt");

    public string SettingsPath { get; } = SettingsPathFor(autoFolder);

    public AutoBackupSettings Load()
    {
        try { return AutoBackupSettings.Parse(files.TryReadAll(SettingsPath) is { } b ? System.Text.Encoding.UTF8.GetString(b) : null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <exception cref="IOException">Or <see cref="UnauthorizedAccessException"/>: the old settings stay.</exception>
    public void Save(AutoBackupSettings settings) =>
        files.ReplaceAtomic(SettingsPath, System.Text.Encoding.UTF8.GetBytes(settings.ToText()));

    /// <param name="gated">After-save backups: skipped when the newest backup in the folder (by file name) is under 24 h old.</param>
    public AutoBackupOutcome Run(string reason, bool gated)
    {
        try
        {
            var settings = Load();
            if (!settings.Enabled) return AutoBackupOutcome.Disabled;
            if (gated && WithinGate()) return AutoBackupOutcome.TooSoon;
            if (files.TryReadAll(configPath) is not { } config) return AutoBackupOutcome.NoConfig;
            service.CreateIn(autoFolder, new BackupContents(config), reason);
            try { service.Prune(autoFolder, settings.Keep); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke($"Automatic backup: couldn't delete old backups: {e.Message}"); }
            return AutoBackupOutcome.Created;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"Automatic backup ({reason}) failed: {e.Message}");
            return AutoBackupOutcome.Failed;
        }
    }

    // A stamp in the future (clock moved back) doesn't hold the gate shut.
    private bool WithinGate() =>
        service.List(autoFolder) is [{ } newest, ..]
        && DateTime.TryParseExact(newest.Stamp, "yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
        && time.GetLocalNow().DateTime - at is { } age && age >= TimeSpan.Zero && age < Gate;
}
