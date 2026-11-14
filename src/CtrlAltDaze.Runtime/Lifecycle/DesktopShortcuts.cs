using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Runtime.Lifecycle;

/// <summary>What a .lnk holds that the scan reads and the writer sets.</summary>
public sealed record ShellLinkData(string Target, string Arguments, string? IconPath = null, string? Description = null,
    string? WorkingDirectory = null);

/// <summary>The .lnk files themselves: IShellLinkW in the engine, a dictionary in tests.</summary>
public interface IShellLinkStore
{
    /// <summary>The .lnk files in <paramref name="directory"/>, and in its subfolders too when <paramref name="recursive"/>.</summary>
    IEnumerable<string> Links(string directory, bool recursive = false);

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
/// <param name="IconFolder"><c>%LOCALAPPDATA%\CtrlAltDaze\shortcuts</c>.</param>
/// <param name="Target">What a new shortcut starts: the Velopack stub when installed, else this exe.</param>
/// <param name="OurTargets">Every exe whose links count as ours (the stub and this exe).</param>
/// <param name="ConfigArgument">The config this engine was started with; null for the default one.</param>
/// <param name="DefaultConfig">The default config's path, so a link naming it counts as the default.</param>
public sealed record ShortcutPlaces(string Desktop, string IconFolder, string Target, IReadOnlyCollection<string> OurTargets,
    string? ConfigArgument, string DefaultConfig)
{
    /// <summary>
    /// Where copies of our shortcuts may also be, searched with their subfolders, only to put icons back (D-106): the
    /// Start menu, taskbar pins and Startup, the places Velopack's updater rewrites besides the Desktop.
    /// </summary>
    public IReadOnlyList<string> CopyFolders { get; init; } = [];

    /// <summary>
    /// The old app's stub and engine (T29.2, D-152): their links count as ours and <see cref="DesktopShortcuts.RepairIcons"/>
    /// re-points them at <see cref="Target"/>. Set only for the installed copy.
    /// </summary>
    public IReadOnlyCollection<string> OldTargets { get; init; } = [];
}

/// <summary>
/// A desktop shortcut per flow (D-049): a .lnk to the engine with <c>--run &lt;flow id&gt;</c>. No state of its own:
/// the Desktop is scanned for links with our target and <c>--run</c>, so a shortcut the user deleted stays deleted.
/// File names are made safe from the flow's name (the .lnk) and id (the .ico); the real id is in the arguments.
/// </summary>
public sealed partial class DesktopShortcuts(ShortcutPlaces places, IShellLinkStore links, IFlowIconRenderer icons, Action<string>? log = null)
{
    /// <summary>Pipe requests run concurrently: scan, pick a name, write and delete happen as one step.</summary>
    private readonly Lock _gate = new();

    /// <summary>Our shortcuts for this engine's config, in file-name order.</summary>
    public IReadOnlyList<DesktopShortcut> List()
    {
        lock (_gate)
            return [.. Scan(anyConfig: false).OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Writes the flow's shortcut under its current name, replacing the one it had (an old name), and returns it. An icon
    /// that can't be drawn leaves the shortcut with the engine's own icon (fail-soft).
    /// </summary>
    public DesktopShortcut Create(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        lock (_gate)
            return CreateLocked(flow);
    }

    private DesktopShortcut CreateLocked(Flow flow)
    {
        var mine = Scan(anyConfig: false).Where(s => s.FlowId == flow.Id).ToList();
        var path = FreePath(flow, mine);
        WriteLink(path, flow);
        foreach (var old in mine.Where(s => !SamePath(s.Path, path)))
            links.Delete(old.Path);
        return new DesktopShortcut(flow.Id, path);
    }

    /// <summary>
    /// Puts each shortcut's flow icon back (on the Desktop and in <see cref="ShortcutPlaces.CopyFolders"/>) where something else replaced it (Velopack's updater re-points every link
    /// to the app's folder at the main exe's icon, D-106) or its .ico is gone; name and place stay. How many it fixed.
    /// </summary>
    public int RepairIcons(IEnumerable<Flow> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);
        var byId = new Dictionary<string, Flow>(StringComparer.Ordinal);
        foreach (var flow in flows)
            byId.TryAdd(flow.Id, flow);
        lock (_gate)
        {
            var fixedCount = 0;
            var all = Scan(anyConfig: false).Concat(places.CopyFolders.SelectMany(f => Scan(anyConfig: false, f, recursive: true)));
            foreach (var shortcut in all.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (!byId.TryGetValue(shortcut.FlowId, out var flow) || links.Read(shortcut.Path) is not { } link)
                    continue;
                var icon = IconPath(flow.Id);
                var old = places.OldTargets.Any(t => SamePath(t, link.Target));
                if (!old && link.IconPath is { } current && SamePath(current, icon) && File.Exists(icon))
                    continue;
                if (WriteLink(shortcut.Path, flow))
                    fixedCount++;
            }
            return fixedCount;
        }
    }

    /// <summary>
    /// Draws the flow's icon and writes its link at <paramref name="path"/>; false when the icon couldn't be drawn, which
    /// leaves the shortcut with the engine's own icon (fail-soft).
    /// </summary>
    private bool WriteLink(string path, Flow flow)
    {
        string? icon = IconPath(flow.Id);
        try
        {
            Directory.CreateDirectory(places.IconFolder);
            icons.Write(icon, FlowGlyph.Decode(FlowGlyph.CodeOf(flow)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            log?.Invoke($"shortcuts: no icon for {flow.Id}: {e.Message}");
            icon = null;
        }
        links.Write(path, new ShellLinkData(places.Target, EngineArguments.Format(flow.Id, places.ConfigArgument), icon,
            $"Runs {flow.Name} in Ctrl Alt Daze", Path.GetDirectoryName(places.Target)));
        return icon is not null;
    }

    /// <summary>Removes the flow's shortcuts (and its icon); how many there were.</summary>
    public int Remove(string flowId)
    {
        lock (_gate)
        {
            var mine = Scan(anyConfig: false).Where(s => s.FlowId == flowId).ToList();
            foreach (var shortcut in mine)
                links.Delete(shortcut.Path);
            // The icon's name carries this config and the exact id: no other shortcut uses it.
            if (mine.Count > 0)
                DeleteIcon(flowId);
            return mine.Count;
        }
    }

    /// <summary>Uninstall: every shortcut of ours, whatever its config, and the icon folder.</summary>
    public int RemoveAll()
    {
        lock (_gate)
        {
            var all = Scan(anyConfig: true).ToList();
            foreach (var shortcut in all)
                links.Delete(shortcut.Path);
            if (Directory.Exists(places.IconFolder))
                Directory.Delete(places.IconFolder, recursive: true);
            return all.Count;
        }
    }

    /// <summary>
    /// The flow's .ico: its id made safe, plus a hash of this config and the exact id, so <c>tv</c> and <c>TV</c>, or one
    /// id in two configs, never share (or delete) each other's icon.
    /// </summary>
    internal string IconPath(string flowId)
    {
        var config = Path.GetFullPath(places.ConfigArgument ?? places.DefaultConfig).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(config + "\n" + flowId));
        var stem = ShortcutNames.SafeName(flowId, "flow");
        return Path.Combine(places.IconFolder, $"{stem} {Convert.ToHexStringLower(hash.AsSpan(0, 4))}.ico");
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
            // A folder called "TV.lnk" takes the name too.
            if (mine.Any(s => SamePath(s.Path, path)) || !(File.Exists(path) || Directory.Exists(path)))
                return path;
        }
        throw new IOException($"Too many files on the desktop are called {name}.");
    }

    private IEnumerable<DesktopShortcut> Scan(bool anyConfig, string? folder = null, bool recursive = false)
    {
        folder ??= places.Desktop;
        if (!Directory.Exists(folder))
            yield break;
        foreach (var path in links.Links(folder, recursive))
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
        target.Length > 0 && places.OurTargets.Concat(places.OldTargets).Any(t => SamePath(t, target));

    private bool SameConfig(string? a, string? b) =>
        SamePath(a ?? places.DefaultConfig, b ?? places.DefaultConfig);

    private void DeleteIcon(string flowId)
    {
        var icon = IconPath(flowId);
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
