using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CtrlAltDaze.Core.Backup;

/// <summary>The .dcbackup zip layout (D-103): manifest.yaml first, config.yaml byte for byte, optional editor.json / history.jsonl.</summary>
public static partial class BackupFormat
{
    public const int CurrentFormat = 1;
    public const string Extension = ".dcbackup";
    public const string ManifestName = "manifest.yaml";
    public const string ConfigName = "config.yaml";
    public const string EditorName = "editor.json";
    public const string HistoryName = "history.jsonl";

    /// <summary>No entry of a real backup comes near this; bounds a zip bomb (config is a few hundred KB at most).</summary>
    private const long MaxEntryBytes = 64L * 1024 * 1024;

    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] PayloadNames = [ConfigName, EditorName, HistoryName];

    public static byte[] Write(BackupContents contents, DateTimeOffset created, string app, string machine, string reason, out BackupManifest manifest)
    {
        var payload = new List<(string Name, byte[] Data)> { (ConfigName, contents.Config) };
        if (contents.EditorJson is { } e) payload.Add((EditorName, e));
        if (contents.History is { } h) payload.Add((HistoryName, h));

        var entries = payload.Select(p => new BackupEntryInfo(p.Name, p.Data.Length, Hash(p.Data))).ToList();
        manifest = new BackupManifest(CurrentFormat, app, ConfigVersionOf(contents.Config), created, machine, reason, entries);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, ManifestName, Encoding.UTF8.GetBytes(WriteManifest(manifest)), created);
            foreach (var (name, data) in payload) Add(zip, name, data, created);
        }
        return ms.ToArray();
    }

    /// <summary>Opens and fully verifies a backup. Only the known entry names are read, in memory, never extracted by path (zip-slip).</summary>
    public static BackupArchive Open(byte[] file)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (!byName.TryAdd(entry.FullName, entry)) throw Damaged();
            }

            if (!byName.TryGetValue(ManifestName, out var mEntry))
                throw new BackupException(BackupFailure.NotABackup, "This isn't a Ctrl Alt Daze backup.");
            var manifest = ParseManifest(Encoding.UTF8.GetString(ReadEntry(mEntry)));

            if (manifest.Format > CurrentFormat)
                throw new BackupException(BackupFailure.TooNew,
                    $"Made by a newer Ctrl Alt Daze ({manifest.App}). Update it, then restore.");

            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var info in manifest.Entries)
            {
                if (!PayloadNames.Contains(info.Name) || !byName.TryGetValue(info.Name, out var entry))
                    throw Damaged();
                var bytes = ReadEntry(entry);
                if (bytes.Length != info.Bytes || !Hash(bytes).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Damaged();
                data[info.Name] = bytes;
            }
            // Anything the manifest does not vouch for (including ../ or absolute names) fails the whole file.
            if (!data.ContainsKey(ConfigName) || byName.Keys.Any(n => n != ManifestName && !data.ContainsKey(n)))
                throw Damaged();
            return new BackupArchive(manifest, data);
        }
        catch (InvalidDataException) { throw new BackupException(BackupFailure.NotABackup, "This isn't a Ctrl Alt Daze backup."); }
        catch (NotSupportedException) { throw new BackupException(BackupFailure.NotABackup, "This isn't a Ctrl Alt Daze backup."); }
    }

    private static BackupException Damaged() => new(BackupFailure.Damaged, "This backup is damaged. Nothing was changed.");

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes) throw Damaged();
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > MaxEntryBytes) throw Damaged();
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] data, DateTimeOffset when)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = when < ZipEpoch ? ZipEpoch : when; // zip cannot hold dates before 1980
        using var s = entry.Open();
        s.Write(data);
    }

    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Reads the config's `version:` line without parsing the file (it is copied verbatim); 0 when absent.</summary>
    public static int ConfigVersionOf(byte[] config)
    {
        var m = ConfigVersionLine().Match(Encoding.UTF8.GetString(config));
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    // manifest.yaml: flat, fixed keys, written and read by hand so no YAML static context is needed.
    internal static string WriteManifest(BackupManifest m)
    {
        var sb = new StringBuilder();
        sb.Append("format: ").Append(m.Format.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("app: ").Append(Quote(m.App)).Append('\n');
        sb.Append("configVersion: ").Append(m.ConfigVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("created: ").Append(Quote(m.Created.ToString("o", CultureInfo.InvariantCulture))).Append('\n');
        sb.Append("machine: ").Append(Quote(m.Machine)).Append('\n');
        sb.Append("reason: ").Append(Quote(m.Reason)).Append('\n');
        sb.Append("contents:\n");
        foreach (var e in m.Entries)
            sb.Append("  ").Append(e.Name).Append(": { bytes: ").Append(e.Bytes.ToString(CultureInfo.InvariantCulture))
              .Append(", sha256: \"").Append(e.Sha256).Append("\" }\n");
        return sb.ToString();
    }

    internal static BackupManifest ParseManifest(string text)
    {
        var top = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<BackupEntryInfo>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            var em = EntryLine().Match(line);
            if (em.Success)
            {
                if (!long.TryParse(em.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)) throw Damaged();
                entries.Add(new BackupEntryInfo(em.Groups[1].Value, bytes, em.Groups[3].Value));
                continue;
            }
            var tm = TopLine().Match(line);
            if (tm.Success) top[tm.Groups[1].Value] = Unquote(tm.Groups[2].Value.Trim());
        }

        if (!top.TryGetValue("format", out var f) || !int.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var format))
            throw new BackupException(BackupFailure.NotABackup, "This isn't a Ctrl Alt Daze backup.");
        var app = top.GetValueOrDefault("app", "");
        if (format > CurrentFormat) return new BackupManifest(format, app, 0, default, "", "", entries); // the rest may differ in a newer format

        _ = DateTimeOffset.TryParse(top.GetValueOrDefault("created"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created);
        _ = int.TryParse(top.GetValueOrDefault("configVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out var cv);
        return new BackupManifest(format, app, cv, created, top.GetValueOrDefault("machine", ""), top.GetValueOrDefault("reason", ""), entries);
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c is '\\' or '"') sb.Append('\\').Append(c);
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var sb = new StringBuilder();
        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length - 1) i++;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^version:\s*(\d+)\s*(#.*)?$", RegexOptions.Multiline)]
    private static partial Regex ConfigVersionLine();

    [GeneratedRegex(@"^\s+([A-Za-z0-9_.\-]+):\s*\{\s*bytes:\s*(\d+),\s*sha256:\s*""([0-9a-fA-F]{64})""\s*\}\s*$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]*):\s*(.*)$")]
    private static partial Regex TopLine();
}
