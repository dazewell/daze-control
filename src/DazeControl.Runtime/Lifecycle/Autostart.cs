using Microsoft.Win32;

namespace DazeControl.Runtime.Lifecycle;

public enum AutostartState
{
    Off,
    On,

    /// <summary>Our Run value exists, but the user turned it off in Task Manager or Settings → Startup apps.</summary>
    DisabledByWindows,
}

/// <summary>What the registry says right now; <see cref="Path"/> is the exe our Run value starts, if any.</summary>
public sealed record AutostartStatus(AutostartState State, string? Path);

/// <summary>
/// "Start with Windows" as an HKCU Run value pointing at the engine (D-015, ported from OSD Autostart). The registry is
/// the only source of truth (OSD D-015): nothing is stored in config.yaml, so Task Manager edits show up.
/// </summary>
public sealed class Autostart
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "DazeControl";

    private readonly string _runKey;
    private readonly string _approvedKey;

    public Autostart(string runKey = DefaultRunKey, string approvedKey = DefaultApprovedKey)
    {
        _runKey = runKey;
        _approvedKey = approvedKey;
    }

    public AutostartStatus Read()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_runKey);
        if (run?.GetValue(ValueName) is not string command)
            return new AutostartStatus(AutostartState.Off, null);
        var path = ExePathOf(command);
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey);
        // Explorer writes 12 bytes; an odd first byte (03, 07) means turned off, even (02, 06) on. No value = on.
        var disabled = approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
        return new AutostartStatus(disabled ? AutostartState.DisabledByWindows : AutostartState.On, path);
    }

    /// <summary>
    /// Points the Run value at <paramref name="exePath"/> (with <paramref name="configPath"/> as its argument, when
    /// the engine runs a non-default config) and clears a Task Manager "disabled".
    /// </summary>
    public void Enable(string exePath, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var command = configPath is null ? $"\"{exePath}\"" : $"\"{exePath}\" \"{configPath}\"";
        using (var run = Registry.CurrentUser.CreateSubKey(_runKey))
            run.SetValue(ValueName, command, RegistryValueKind.String);
        DeleteValue(_approvedKey);
    }

    public void Disable()
    {
        DeleteValue(_runKey);
        DeleteValue(_approvedKey);
    }

    /// <summary>
    /// Uninstall (OSD D-029, T10.1): turns autostart off only if the Run value starts an exe inside
    /// <paramref name="directory"/>, so removing the installed copy never touches a Debug copy's entry.
    /// </summary>
    public bool DisableIfUnder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (Read().Path is not { } path)
            return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        Disable();
        return true;
    }

    /// <summary>The exe of a Run command: the first quoted token, else up to the first space.</summary>
    internal static string ExePathOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            return close < 0 ? command[1..] : command[1..close];
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static void DeleteValue(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
