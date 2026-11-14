using CtrlAltDaze.Core.Backup;

namespace CtrlAltDaze.Core.Updates;

/// <summary>Which releases this PC follows (D-108): only stable ones, or release candidates first.</summary>
public enum UpdateChannel { Stable, ReleaseCandidates }

public static class UpdateChannels
{
    /// <summary>What the editor and <c>cad</c> call it, in plain words.</summary>
    public static string Label(this UpdateChannel channel) => channel == UpdateChannel.ReleaseCandidates ? "Release candidates" : "Stable";

    /// <summary>The word <c>cad update --channel</c> and the settings file use.</summary>
    public static string Word(this UpdateChannel channel) => channel == UpdateChannel.ReleaseCandidates ? "rc" : "stable";

    /// <summary>Case-insensitive <c>stable</c> or <c>rc</c>; anything else is null.</summary>
    public static UpdateChannel? FromWord(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "stable" => UpdateChannel.Stable,
        "rc" => UpdateChannel.ReleaseCandidates,
        _ => null,
    };

    /// <summary>The GitHub source lists a pre-release (a tag like v0.52.0-rc.1) only when asked; a stable release of the same or a higher number still wins by version order.</summary>
    public static bool IncludesPreReleases(this UpdateChannel channel) => channel == UpdateChannel.ReleaseCandidates;
}

/// <summary>
/// The per-user channel (D-108): one line, <c>channel=stable</c>, in <c>%LOCALAPPDATA%\CtrlAltDaze\update-channel.txt</c>. Not in
/// config.yaml, so a restore, a hot reload or a copied config never changes what this PC follows. Anything unreadable is Stable.
/// </summary>
public sealed class UpdateChannelStore(string filePath, IBackupFiles? files = null)
{
    private readonly IBackupFiles _files = files ?? new PhysicalBackupFiles();

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName, "update-channel.txt");

    public string FilePath { get; } = filePath;

    public static UpdateChannel Parse(string? text)
    {
        foreach (var line in (text ?? "").Split('\n'))
        {
            var i = line.IndexOf('=', StringComparison.Ordinal);
            if (i > 0 && line[..i].Trim().Equals("channel", StringComparison.OrdinalIgnoreCase) && UpdateChannels.FromWord(line[(i + 1)..]) is { } channel)
                return channel;
        }
        return UpdateChannel.Stable;
    }

    public static string ToText(UpdateChannel channel) => $"channel={channel.Word()}\n";

    public UpdateChannel Load()
    {
        try { return Parse(_files.TryReadAll(FilePath) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return UpdateChannel.Stable; }
    }

    /// <exception cref="IOException">Or <see cref="UnauthorizedAccessException"/>: the old choice stays.</exception>
    public void Save(UpdateChannel channel) => _files.ReplaceAtomic(FilePath, System.Text.Encoding.UTF8.GetBytes(ToText(channel)));
}
