using System.IO.Compression;
using System.Text;
using DazeControl.Core.Backup;

namespace DazeControl.Core.Tests;

public class BackupTests
{
    private sealed class MemFiles : IBackupFiles
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => Files.ContainsKey(path);
        public byte[] ReadAll(string path) => Files[path];
        public void WriteAtomic(string path, byte[] data) => Files[path] = data;
        public void Delete(string path) => Files.Remove(path);
        public IReadOnlyList<string> ListFiles(string folder) =>
            Files.Keys.Where(k => Path.GetDirectoryName(k) == folder).ToList();
    }

    private const string Dir = @"X:\backups";
    private static readonly byte[] Config = Encoding.UTF8.GetBytes("# my hand comment\r\nversion: 1\nflows: []\n");

    private static BackupService NewService(MemFiles files, TimeProvider? time = null) =>
        new(files, time ?? new FixedTime(new DateTimeOffset(2026, 11, 6, 21, 30, 0, TimeSpan.Zero)), "0.46.0", "DAZE-PC");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static byte[] RawZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, d) in entries)
            {
                using var s = zip.CreateEntry(n).Open();
                s.Write(d);
            }
        return ms.ToArray();
    }

    private static string Manifest(int format, byte[] config, string extra = "") =>
        $"format: {format}\napp: \"0.3.0\"\nconfigVersion: 1\ncreated: \"2026-01-02T03:04:05+00:00\"\nmachine: \"OLD\"\nreason: \"manual\"\n{extra}contents:\n" +
        $"  config.yaml: {{ bytes: {config.Length}, sha256: \"{BackupFormat.Hash(config)}\" }}\n";

    [Fact]
    public void RoundTripKeepsConfigByteForByte()
    {
        var files = new MemFiles();
        var svc = NewService(files);
        var editor = Encoding.UTF8.GetBytes("{\"nav\":1}");
        var r = svc.CreateIn(Dir, new BackupContents(Config, editor), BackupService.Manual);

        Assert.Equal(Path.Combine(Dir, "DazeControl-backup-2026-11-06-2130.dcbackup"), r.Path);
        var open = svc.Open(r.Path);
        Assert.Equal(Config, open.Config);
        Assert.Equal(editor, open.EditorJson);
        Assert.Null(open.History);
        Assert.Equal(1, open.Manifest.Format);
        Assert.Equal("0.46.0", open.Manifest.App);
        Assert.Equal("DAZE-PC", open.Manifest.Machine);
        Assert.Equal("manual", open.Manifest.Reason);
        Assert.Equal(1, open.Manifest.ConfigVersion);
        Assert.Equal(2, open.Manifest.Entries.Count);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ManifestIsTheFirstEntryAndNeverOverwritesAFile()
    {
        var files = new MemFiles();
        var svc = NewService(files);
        var a = svc.CreateIn(Dir, new BackupContents(Config), "manual");
        var b = svc.CreateIn(Dir, new BackupContents(Config), "manual");
        Assert.NotEqual(a.Path, b.Path);
        Assert.EndsWith("-2130-2.dcbackup", b.Path);
        using var zip = new ZipArchive(new MemoryStream(files.Files[a.Path]));
        Assert.Equal("manifest.yaml", zip.Entries[0].FullName);
    }

    [Fact]
    public void TamperedEntryIsDamaged()
    {
        var files = new MemFiles();
        var svc = NewService(files);
        var good = BackupFormat.Write(new BackupContents(Config), DateTimeOffset.UnixEpoch, "1", "m", "manual", out _);
        using var src = new ZipArchive(new MemoryStream(good));
        var manifest = new StreamReader(src.GetEntry("manifest.yaml")!.Open()).ReadToEnd();
        var evil = RawZip(("manifest.yaml", Encoding.UTF8.GetBytes(manifest)), ("config.yaml", Encoding.UTF8.GetBytes("version: 1\nflows: [evil]\n")));
        var ex = Assert.Throws<BackupException>(() => BackupFormat.Open(evil));
        Assert.Equal(BackupFailure.Damaged, ex.Kind);
    }

    [Fact]
    public void MissingEntryAndUnlistedEntryAreDamaged()
    {
        var m = Encoding.UTF8.GetBytes(Manifest(1, Config));
        Assert.Equal(BackupFailure.Damaged, Assert.Throws<BackupException>(() => BackupFormat.Open(RawZip(("manifest.yaml", m)))).Kind);
        Assert.Equal(BackupFailure.Damaged, Assert.Throws<BackupException>(() =>
            BackupFormat.Open(RawZip(("manifest.yaml", m), ("config.yaml", Config), ("extra.txt", [1])))).Kind);
    }

    [Theory]
    [InlineData("../evil.yaml")]
    [InlineData("..\\evil.yaml")]
    [InlineData("/abs/evil.yaml")]
    [InlineData("C:\\evil.yaml")]
    [InlineData("sub/config.yaml")]
    public void PathTraversalNamesAreRefused(string name)
    {
        var m = Encoding.UTF8.GetBytes(Manifest(1, Config));
        var ex = Assert.Throws<BackupException>(() => BackupFormat.Open(RawZip(("manifest.yaml", m), ("config.yaml", Config), (name, [1]))));
        Assert.Equal(BackupFailure.Damaged, ex.Kind);
    }

    [Fact]
    public void ManifestListingATraversalNameIsRefused()
    {
        var data = new byte[] { 1 };
        var m = Manifest(1, Config) + $"  ../evil.yaml: {{ bytes: 1, sha256: \"{BackupFormat.Hash(data)}\" }}\n";
        // the entry regex does not even accept the name, so the entry is simply not vouched for
        var ex = Assert.Throws<BackupException>(() => BackupFormat.Open(RawZip(("manifest.yaml", Encoding.UTF8.GetBytes(m)), ("config.yaml", Config), ("../evil.yaml", data))));
        Assert.Equal(BackupFailure.Damaged, ex.Kind);
    }

    [Fact]
    public void NewerFormatIsRefusedBeforeAnythingElse()
    {
        var m = Encoding.UTF8.GetBytes("format: 2\napp: \"0.99.0\"\nsomethingNew: 1\n");
        var ex = Assert.Throws<BackupException>(() => BackupFormat.Open(RawZip(("manifest.yaml", m))));
        Assert.Equal(BackupFailure.TooNew, ex.Kind);
        Assert.Contains("0.99.0", ex.Message);
    }

    [Fact]
    public void NotAZipOrNoManifestIsNotABackup()
    {
        Assert.Equal(BackupFailure.NotABackup, Assert.Throws<BackupException>(() => BackupFormat.Open("hello"u8.ToArray())).Kind);
        Assert.Equal(BackupFailure.NotABackup, Assert.Throws<BackupException>(() => BackupFormat.Open(RawZip(("config.yaml", Config)))).Kind);
        Assert.Equal(BackupFailure.NotABackup, Assert.Throws<BackupException>(() => BackupFormat.Open(RawZip(("manifest.yaml", "nope"u8.ToArray())))).Kind);
    }

    [Fact]
    public void OldVersionFixtureStillOpens()
    {
        // Hand-built like an early build wrote it: an older app, an unknown manifest key and a counts line this reader ignores.
        var cfg = Encoding.UTF8.GetBytes("version: 1\nflows:\n  - id: a\n");
        var m = Manifest(1, cfg, "legacyKey: yes\n") + "counts: { flows: 1, library: 0 }\n";
        var open = BackupFormat.Open(RawZip(("manifest.yaml", Encoding.UTF8.GetBytes(m)), ("config.yaml", cfg)));
        Assert.Equal(cfg, open.Config);
        Assert.Equal("0.3.0", open.Manifest.App);
        Assert.Equal("OLD", open.Manifest.Machine);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), open.Manifest.Created);
    }

    [Fact]
    public void ManifestQuotesAwkwardMachineNames()
    {
        var files = new MemFiles();
        var svc = new BackupService(files, new FixedTime(DateTimeOffset.UnixEpoch), "1.0", "PC \"A\"\\x\n");
        var r = svc.CreateIn(Dir, new BackupContents(Config), "manual");
        Assert.Equal("PC \"A\"\\x", svc.Open(r.Path).Manifest.Machine);
    }

    [Fact]
    public void SecretLookingValuesWarnButStillWrite()
    {
        var cfg = Encoding.UTF8.GetBytes("version: 1\nheaders:\n  Authorization: Bearer abcdef123456\npassword: hunter2\ncredential: github-token\n");
        var files = new MemFiles();
        var r = NewService(files).CreateIn(Dir, new BackupContents(cfg), "manual");
        Assert.True(files.Exists(r.Path));
        Assert.Equal(2, r.Warnings.Count);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("hunter2") || w.Contains("abcdef"));
    }

    [Fact]
    public void CreateAtKeepsTheChosenNameAndAddsACounter()
    {
        var files = new MemFiles();
        var svc = NewService(files);
        var p = Path.Combine(Dir, "mine.dcbackup");
        Assert.Equal(p, svc.CreateAt(p, new BackupContents(Config), "manual").Path);
        Assert.Equal(Path.Combine(Dir, "mine-2.dcbackup"), svc.CreateAt(p, new BackupContents(Config), "manual").Path);
    }

    [Fact]
    public void PruneKeepsTheNewestAndLeavesForeignFiles()
    {
        var files = new MemFiles();
        var clock = new FixedTime(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var svc = NewService(files, clock);
        var paths = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            paths.Add(svc.CreateIn(Dir, new BackupContents(Config), "auto-save").Path);
            clock.Now = clock.Now.AddDays(1);
        }
        paths.Add(svc.CreateIn(Dir, new BackupContents(Config), "auto-save").Path);              // same minute as the last one: counter -2
        files.Files[Path.Combine(Dir, "notes.txt")] = [1];
        files.Files[Path.Combine(@"X:\other", "DazeControl-backup-2020-01-01-0000.dcbackup")] = [1];

        var newest = svc.List(Dir);
        Assert.Equal(paths[^1], newest[0].Path);                                                   // -2 counts as newer than its base name
        Assert.Equal(6, newest.Count);

        var deleted = svc.Prune(Dir, 3);
        Assert.Equal(3, deleted.Count);
        Assert.Equal(newest.Take(3).Select(f => f.Path), svc.List(Dir).Select(f => f.Path));
        Assert.True(files.Exists(Path.Combine(Dir, "notes.txt")));
        Assert.True(files.Exists(Path.Combine(@"X:\other", "DazeControl-backup-2020-01-01-0000.dcbackup")));
        svc.Prune(Dir, 0);                                                                         // keep < 1 still keeps one
        Assert.Single(svc.List(Dir));
    }

    [Fact]
    public void PhysicalFilesWriteAtomicallyInATempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc-backup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new BackupService(new PhysicalBackupFiles(), TimeProvider.System, "1.0", "m");
            var r = svc.CreateIn(dir, new BackupContents(Config), "manual");
            Assert.Equal(Config, svc.Open(r.Path).Config);
            Assert.Single(Directory.GetFiles(dir));
            Assert.Single(svc.List(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
