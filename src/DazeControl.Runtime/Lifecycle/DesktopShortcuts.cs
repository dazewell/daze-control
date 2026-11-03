using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DazeControl.Core.Model;

namespace DazeControl.Runtime.Lifecycle;

/// <summary>What a .lnk holds that the scan reads and the writer sets.</summary>
public sealed record ShellLinkData(string Target, string Arguments, string? IconPath = null, string? Description = null,
    string? WorkingDirectory = null);

/// <summary>The .lnk files themselves: IShellLinkW in the engine, a dictionary in tests.</summary>
public interface IShellLinkStore
{
    IEnumerable<string> Links(string directory);

    /// <summary>Null when the file isn't a link that can be read.</summary>
    ShellLinkData? Read(string path);

    void Write(string path, ShellLinkData link);

    void Delete(string path);
}

/// <summary>Draws a flow's glyph on the app tile into an .ico (the engine's DirectWrite path, D-012).</summary>
public interface IFlowIconRenderer
{
    void Write(string path, string glyph);
}

/// <summary>One of our shortcuts on the desktop.</summary>
public sealed record DesktopShortcut(string FlowId, string Path)
{
    /// <summary>The name Explorer shows (the file name without ".lnk").</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>Where the shortcuts go, what they start and which config is ours.</summary>
/// <param name="Desktop">The user's Desktop folder (a OneDrive one too).</param>
/// <param name="IconFolder"><c>%LOCALAPPDATA%\DazeControl\shortcuts</c>.</param>
/// <param name="Target">What a new shortcut starts: the Velopack stub when installed, else this exe.</param>
/// <param name="OurTargets">Every exe whose links count as ours (the stub and this exe).</param>
/// <param name="ConfigArgument">The config this engine was started with; null for the default one.</param>
/// <param name="DefaultConfig">The default config's path, so a link naming it counts as the default.</param>
public sealed record ShortcutPlaces(string Desktop, string IconFolder, string Target, IReadOnlyCollection<string> OurTargets,
    string? ConfigArgument, string DefaultConfig);

/// <summary>
/// A desktop shortcut per flow (D-049): a .lnk to the engine with <c>--run &lt;flow id&gt;</c>. No state of its own:
/// the Desktop is scanned for links with our target and <c>--run</c>, so a shortcut the user deleted stays deleted.
/// File names are made safe from the flow's name (the .lnk) and id (the .ico); the real id is in the arguments.
/// </summary>
public sealed partial class DesktopShortcuts(ShortcutPlaces places, IShellLinkStore links, IFlowIconRenderer icons, Action<string>? log = null)
{
    /// <summary>Our shortcuts for this engine's config, in file-name order.</summary>
    public IReadOnlyList<DesktopShortcut> List() => [.. Scan(anyConfig: false).OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Writes the flow's shortcut under its current name, replacing the one it had (an old name), and returns it. An icon
    /// that can't be drawn leaves the shortcut with the engine's own icon (fail-soft).
    /// </summary>
    public DesktopShortcut Create(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var mine = Scan(anyConfig: false).Where(s => s.FlowId == flow.Id).ToList();
        var path = FreePath(flow, mine);
        string? icon = Path.Combine(places.IconFolder, ShortcutNames.SafeName(flow.Id, "flow") + ".ico");
        try
        {
            Directory.CreateDirectory(places.IconFolder);
            icons.Write(icon, FlowGlyph.Decode(flow.Glyph));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            log?.Invoke($"shortcuts: no icon for {flow.Id}: {e.Message}");
            icon = null;
        }
        links.Write(path, new ShellLinkData(places.Target, EngineArguments.Format(flow.Id, places.ConfigArgument), icon,
            $"Runs {flow.Name} in Daze Control", Path.GetDirectoryName(places.Target)));
        foreach (var old in mine.Where(s => !SamePath(s.Path, path)))
            links.Delete(old.Path);
        return new DesktopShortcut(flow.Id, path);
    }

    /// <summary>Removes the flow's shortcuts (and its icon); how many there were.</summary>
    public int Remove(string flowId)
    {
        var mine = Scan(anyConfig: false).Where(s => s.FlowId == flowId).ToList();
        foreach (var shortcut in mine)
            links.Delete(shortcut.Path);
        // Another config's shortcut for the same id may still use the icon.
        if (mine.Count > 0 && !Scan(anyConfig: true).Any(s => s.FlowId == flowId))
            DeleteIcon(flowId);
        return mine.Count;
    }

    /// <summary>Uninstall: every shortcut of ours, whatever its config, and the icon folder.</summary>
    public int RemoveAll()
    {
        var all = Scan(anyConfig: true).ToList();
        foreach (var shortcut in all)
            links.Delete(shortcut.Path);
        if (Directory.Exists(places.IconFolder))
            Directory.Delete(places.IconFolder, recursive: true);
        return all.Count;
    }

    /// <summary>True when the shortcut is called what the flow is called now (or that plus " (2)" for a clash).</summary>
    public static bool HasCurrentName(DesktopShortcut shortcut, Flow flow)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        ArgumentNullException.ThrowIfNull(flow);
        var expected = ShortcutNames.FileName(flow.Name, flow.Id);
        var name = shortcut.Name;
        return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith(expected + " (", StringComparison.OrdinalIgnoreCase) && ClashSuffix().IsMatch(name[expected.Length..]));
    }

    /// <summary>The flow's name as a file; on a clash with something that isn't this flow's shortcut, " (2)" and up.</summary>
    private string FreePath(Flow flow, List<DesktopShortcut> mine)
    {
        var name = ShortcutNames.FileName(flow.Name, flow.Id);
        for (var n = 1; n < 100; n++)
        {
            var path = Path.Combine(places.Desktop, (n == 1 ? name : $"{name} ({n})") + ".lnk");
            if (mine.Any(s => SamePath(s.Path, path)) || !File.Exists(path))
                return path;
        }
        throw new IOException($"Too many files on the desktop are called {name}.");
    }

    private IEnumerable<DesktopShortcut> Scan(bool anyConfig)
    {
        if (!Directory.Exists(places.Desktop))
            yield break;
        foreach (var path in links.Links(places.Desktop))
        {
            if (links.Read(path) is not { } link || !IsOurTarget(link.Target))
                continue;
            var args = EngineArguments.Parse(EngineArguments.Split(link.Arguments));
            if (args.RunFlowId is not { } flowId)
                continue;
            if (anyConfig || SameConfig(args.Config, places.ConfigArgument))
                yield return new DesktopShortcut(flowId, path);
        }
    }

    private bool IsOurTarget(string target) =>
        target.Length > 0 && places.OurTargets.Any(t => SamePath(t, target));

    private bool SameConfig(string? a, string? b) =>
        SamePath(a ?? places.DefaultConfig, b ?? places.DefaultConfig);

    private void DeleteIcon(string flowId)
    {
        var icon = Path.Combine(places.IconFolder, ShortcutNames.SafeName(flowId, "flow") + ".ico");
        try
        {
            File.Delete(icon);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"shortcuts: {icon} not deleted: {e.Message}");
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^ \([0-9]{1,2}\)$")]
    private static partial Regex ClashSuffix();
}

/// <summary>File names from flow names and ids (D-049): Windows' reserved characters and names can't be in them.</summary>
public static partial class ShortcutNames
{
    /// <summary>Long enough for any flow name people use; the Desktop path plus this stays far from MAX_PATH.</summary>
    public const int MaxLength = 80;

    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>The .lnk's name (no extension) for a flow: its name made safe, else its id.</summary>
    public static string FileName(string flowName, string flowId) =>
        SafeName(string.IsNullOrWhiteSpace(flowName) ? flowId : flowName, "Flow", hashOf: flowId);

    /// <summary>
    /// <paramref name="text"/> with reserved characters (and <c>/</c>) as "_", no trailing dots or spaces, never a
    /// reserved device name; when anything changed, a short hash of <paramref name="hashOf"/> keeps two names that
    /// became equal apart.
    /// </summary>
    public static string SafeName(string text, string fallback, string? hashOf = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
            safe.Append(c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c);
        var name = safe.ToString().TrimEnd('.', ' ');
        if (name.Length > MaxLength)
            name = name[..MaxLength].TrimEnd('.', ' ');
        if (name.Length == 0)
            name = fallback;
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
            name = "_" + name;
        if (name == text)
            return name;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashOf ?? text));
        return $"{name} {Convert.ToHexStringLower(hash.AsSpan(0, 3))}";
    }
}
