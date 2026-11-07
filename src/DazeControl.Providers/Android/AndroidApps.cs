using System.Globalization;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Android;

/// <summary>One launchable app: its package id (what a flow stores) and the name shown for it (D-092).</summary>
public sealed record AndroidApp(string Package, string Name);

/// <summary>Reads the launchable apps off the output of <c>cmd package query-activities</c> and names them (D-092).</summary>
public static class AndroidAppList
{
    /// <summary>Both launcher categories (phones, then TV boxes), cut on the device to the two lines per app that matter:
    /// ~85 KB of ResolveInfo text becomes a few KB.</summary>
    public const string ListCommand =
        "for c in LAUNCHER LEANBACK_LAUNCHER; do cmd package query-activities -a android.intent.action.MAIN -c android.intent.category.$c; done"
        + " | grep -E '^ {6}packageName=|^ {6}labelRes='";

    /// <summary>Fallback for devices without <c>query-activities</c>: the user-installed packages, named by guess.</summary>
    public const string PackagesCommand = "pm list packages -3";

    private const string LabelKey = "nonLocalizedLabel=";

    // Well-known ids whose label is only a resource id over adb (D-092).
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["com.google.android.youtube"] = "YouTube",
        ["com.google.android.youtube.tv"] = "YouTube",
        ["com.google.android.youtube.tvmusic"] = "YouTube Music",
        ["com.google.android.apps.youtube.music"] = "YouTube Music",
        ["com.google.android.youtube.tvunplugged"] = "YouTube TV",
        ["com.netflix.ninja"] = "Netflix",
        ["com.netflix.mediaclient"] = "Netflix",
        ["com.amazon.amazonvideo.livingroom"] = "Prime Video",
        ["com.amazon.avod.thirdpartyclient"] = "Prime Video",
        ["com.disney.disneyplus"] = "Disney+",
        ["com.spotify.tv.android"] = "Spotify",
        ["com.spotify.music"] = "Spotify",
        ["org.xbmc.kodi"] = "Kodi",
        ["com.plexapp.android"] = "Plex",
        ["org.videolan.vlc"] = "VLC",
        ["com.mxtech.videoplayer.ad"] = "MX Player",
        ["tv.twitch.android.app"] = "Twitch",
        ["com.android.vending"] = "Google Play Store",
        ["com.android.tv.settings"] = "Settings",
        ["com.android.settings"] = "Settings",
        ["com.android.deskclock"] = "Clock",
        ["com.google.android.apps.photos"] = "Google Photos",
        ["com.google.android.gm"] = "Gmail",
        ["com.google.android.apps.maps"] = "Google Maps",
        ["com.whatsapp"] = "WhatsApp",
        ["org.telegram.messenger"] = "Telegram",
        ["com.instagram.android"] = "Instagram",
        ["com.sec.android.app.sbrowser"] = "Samsung Internet",
        ["com.android.chrome"] = "Chrome",
    };

    // Segments that say nothing about the app when the id has to be read as words.
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
        { "com", "org", "net", "io", "tv", "android", "app", "apps", "mobile", "client", "free", "lite", "ninja", "google" };

    /// <summary>Apps from the filtered <see cref="ListCommand"/> output, sorted by name (D-092).</summary>
    public static IReadOnlyList<AndroidApp> Parse(string listing)
    {
        var literal = new Dictionary<string, string>(StringComparer.Ordinal);
        var packages = new List<string>();
        string? current = null;
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("packageName=", StringComparison.Ordinal))
            {
                current = line["packageName=".Length..].Trim();
                if (IsPackage(current) && !packages.Contains(current))
                    packages.Add(current);
            }
            else if (current is not null && line.StartsWith("labelRes=", StringComparison.Ordinal) && IsPackage(current)
                && line.IndexOf(LabelKey, StringComparison.Ordinal) is var at and >= 0)
            {
                var start = at + LabelKey.Length;
                var end = line.IndexOf(" icon=", start, StringComparison.Ordinal);
                var label = (end < 0 ? line[start..] : line[start..end]).Trim();
                if (label.Length > 0 && label != "null")
                    literal.TryAdd(current, label);
            }
        }
        return Sort(packages.Select(p => new AndroidApp(p, literal.GetValueOrDefault(p) ?? Guess(p))));
    }

    /// <summary>Apps from <c>pm list packages</c> ("package:com.x"), all named by guess.</summary>
    public static IReadOnlyList<AndroidApp> ParsePackages(string listing) =>
        Sort(listing.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => l["package:".Length..].Trim()).Where(IsPackage).Distinct(StringComparer.Ordinal)
            .Select(p => new AndroidApp(p, Guess(p))));

    public static IReadOnlyList<AndroidApp> Sort(IEnumerable<AndroidApp> apps) =>
        [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Package, StringComparer.Ordinal)];

    /// <summary>A package id is letters, digits, <c>_</c> and dots: it goes into a shell line, so nothing else is let through.</summary>
    public static bool IsPackage(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');

    /// <summary>The table's name, else the id's last meaningful segment capitalised ("com.vk.tv" → "Vk").</summary>
    public static string Guess(string package)
    {
        if (Known.TryGetValue(package, out var known))
            return known;
        var parts = package.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var part = parts.LastOrDefault(p => !Filler.Contains(p)) ?? parts.LastOrDefault() ?? package;
        return part.Length == 0 ? package : char.ToUpper(part[0], CultureInfo.InvariantCulture) + part[1..];
    }
}

/// <summary>The last successful app list of each device, one small text file each (D-093); nothing else is kept.</summary>
public sealed class AndroidAppCache(string directory)
{
    public IReadOnlyList<AndroidApp> Load(string serial)
    {
        try
        {
            var path = PathFor(serial);
            if (!File.Exists(path))
                return [];
            return [.. File.ReadAllLines(path)
                .Select(l => l.Split('\t', 2))
                .Where(p => p.Length == 2 && AndroidAppList.IsPackage(p[0]) && p[1].Length > 0)
                .Select(p => new AndroidApp(p[0], p[1]))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return []; // a cache that can't be read is an empty cache
        }
    }

    public void Save(string serial, IReadOnlyList<AndroidApp> apps)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(PathFor(serial), apps.Select(a => a.Package + "\t" + a.Name.Replace('\t', ' ').Replace('\n', ' ')));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not cached; the list was still read.
        }
    }

    private string PathFor(string serial) =>
        Path.Combine(directory, new string([.. serial.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_')]) + ".txt");
}

/// <summary>
/// The app picker's list (D-092, D-093): the cache unless <c>refresh</c>, then connect and read the device and rewrite
/// the cache. Runs adb only on request; keeps nothing in memory.
/// </summary>
public sealed class AndroidAppLister(AndroidRemote remote, IAdb adb, AndroidAppCache cache)
{
    public async Task<IReadOnlyList<AndroidApp>> ListAsync(string address, string? adbPath, bool refresh, CancellationToken cancellationToken)
    {
        var serial = AndroidDevice.SerialOf(address);
        if (!refresh)
            return cache.Load(serial);
        var exe = adbPath ?? AndroidRemote.DefaultAdb;
        await remote.ConnectAsync(exe, serial, cancellationToken).ConfigureAwait(false);
        var result = await adb.RunAsync(exe, ["-s", serial, "shell", AndroidAppList.ListCommand], cancellationToken).ConfigureAwait(false);
        var apps = result.ExitCode == 0 ? AndroidAppList.Parse(result.Output) : [];
        if (apps.Count == 0)
        {
            result = await adb.RunAsync(exe, ["-s", serial, "shell", AndroidAppList.PackagesCommand], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new StepFailedException($"Couldn't read the apps: {AndroidRemote.Reason(result)}");
            apps = AndroidAppList.ParsePackages(result.Output);
        }
        cache.Save(serial, apps);
        return apps;
    }
}
