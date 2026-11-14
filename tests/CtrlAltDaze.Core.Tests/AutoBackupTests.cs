using CtrlAltDaze.Core.Backup;

namespace CtrlAltDaze.Core.Tests;

/// <summary>T19.5 (D-103): the 24 h gate from file names, pruning, fail-soft and the off switch; all in memory.</summary>
public sealed class AutoBackupTests
{
    private const string Config = "/c/config.yaml";
    private const string Auto = "/c/backups/auto";

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Files : IBackupFiles
    {
        public Dictionary<string, byte[]> Data = new(StringComparer.OrdinalIgnoreCase);
        public bool FailWrites;
        public bool FailDeletes;
        public bool Exists(string path) => Data.ContainsKey(path);
        public byte[] ReadAll(string path) => Data[path];
        public bool TryWriteNew(string path, byte[] data)
        {
            if (FailWrites) throw new IOException("disk full");
            return Data.TryAdd(path, data);
        }
        public void Delete(string path)
        {
            if (FailDeletes) throw new IOException("locked");
            Data.Remove(path);
        }
        public byte[]? TryReadAll(string path) => Data.GetValueOrDefault(path);
        public void ReplaceAtomic(string path, byte[] data) => Data[path] = data;
        public IReadOnlyList<string> ListFiles(string folder) =>
            [.. Data.Keys.Where(k => k.Replace('\\', '/').StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))];
    }

    private readonly Files _files = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _log = [];

    private AutoBackup Make()
    {
        _files.Data[Config] = "version: 1\n"u8.ToArray();
        return new AutoBackup(new BackupService(_files, _clock, "1.0.0", "PC"), _files, _clock, Config, Auto, _log.Add);
    }

    private int Count => _files.Data.Keys.Count(k => k.EndsWith(".dcbackup", StringComparison.Ordinal));

    [Fact]
    public void FirstSaveBacksUpAndSecondWithin24HoursDoesNot()
    {
        var auto = Make();
        Assert.Equal(AutoBackupOutcome.Created, auto.Run(BackupService.AutoSave, gated: true));
        _clock.Now = _clock.Now.AddHours(23).AddMinutes(58);
        Assert.Equal(AutoBackupOutcome.TooSoon, auto.Run(BackupService.AutoSave, gated: true));
        Assert.Equal(1, Count);
    }

    [Fact]
    public void GateOpensAfter24Hours()
    {
        var auto = Make();
        auto.Run(BackupService.AutoSave, gated: true);
        _clock.Now = _clock.Now.AddHours(24);
        Assert.Equal(AutoBackupOutcome.Created, auto.Run(BackupService.AutoSave, gated: true));
        Assert.Equal(2, Count);
    }

    [Fact]
    public void StampInTheFutureDoesNotHoldTheGateShut()
    {
        var auto = Make();
        auto.Run(BackupService.AutoSave, gated: true);
        _clock.Now = _clock.Now.AddDays(-3);
        Assert.Equal(AutoBackupOutcome.Created, auto.Run(BackupService.AutoSave, gated: true));
    }

    [Fact]
    public void BeforeUpdateIgnoresTheGate()
    {
        var auto = Make();
        auto.Run(BackupService.AutoSave, gated: true);
        Assert.Equal(AutoBackupOutcome.Created, auto.Run(BackupService.BeforeUpdate, gated: false));
        Assert.Equal(2, Count);
    }

    [Fact]
    public void OnlyTheNewestKeepAreLeft()
    {
        var auto = Make();
        auto.Save(new AutoBackupSettings(true, 3));
        for (var i = 0; i < 6; i++)
        {
            _clock.Now = _clock.Now.AddDays(1);
            Assert.Equal(AutoBackupOutcome.Created, auto.Run(BackupService.AutoSave, gated: true));
        }
        Assert.Equal(3, Count);
        Assert.Contains(_files.Data.Keys, k => k.Contains("2026-11-13", StringComparison.Ordinal));
        Assert.DoesNotContain(_files.Data.Keys, k => k.Contains("2026-11-08", StringComparison.Ordinal));
    }

    [Fact]
    public void TurnedOffWritesNothing()
    {
        var auto = Make();
        auto.Save(new AutoBackupSettings(false, 10));
        Assert.Equal(AutoBackupOutcome.Disabled, auto.Run(BackupService.AutoSave, gated: true));
        Assert.Equal(AutoBackupOutcome.Disabled, auto.Run(BackupService.BeforeUpdate, gated: false));
        Assert.Equal(0, Count);
    }

    [Fact]
    public void DefaultsAreOnAndTenAndBadSettingsFallBack()
    {
        var auto = Make();
        Assert.Equal(new AutoBackupSettings(true, 10), auto.Load());
        _files.Data[auto.SettingsPath] = "garbage keep=abc\nenabled=maybe"u8.ToArray();
        Assert.Equal(new AutoBackupSettings(true, 10), auto.Load());
        _files.Data[auto.SettingsPath] = "enabled=false\nkeep=999"u8.ToArray();
        Assert.Equal(new AutoBackupSettings(false, 50), auto.Load());
        Assert.Equal(3, AutoBackupSettings.Parse("keep=1").Keep);
    }

    [Fact]
    public void FailedWriteIsLoggedNotThrown()
    {
        var auto = Make();
        _files.FailWrites = true;
        Assert.Equal(AutoBackupOutcome.Failed, auto.Run(BackupService.BeforeUpdate, gated: false));
        Assert.Single(_log);
    }

    [Fact]
    public void FailedPruneStillCountsAsCreated()
    {
        var auto = Make();
        auto.Save(new AutoBackupSettings(true, 3));
        for (var i = 0; i < 3; i++)
        {
            _clock.Now = _clock.Now.AddDays(1);
            auto.Run(BackupService.AutoSave, gated: true);
        }
        _files.FailDeletes = true;
        _clock.Now = _clock.Now.AddDays(1);
        Assert.Equal(AutoBackupOutcome.Created, auto.Run(BackupService.AutoSave, gated: true));
        Assert.Single(_log);
    }

    [Fact]
    public void MissingConfigIsSkipped()
    {
        var auto = Make();
        _files.Data.Remove(Config);
        Assert.Equal(AutoBackupOutcome.NoConfig, auto.Run(BackupService.AutoSave, gated: true));
    }

    [Fact]
    public void AThrowingLoggerDoesNotBreakTheOutcome()
    {
        _files.Data[Config] = "version: 1"u8.ToArray();
        var auto = new AutoBackup(new BackupService(_files, _clock, "1.0.0", "PC"), _files, _clock, Config, Auto, _ => throw new IOException("closed"));
        _files.FailWrites = true;
        Assert.Equal(AutoBackupOutcome.Failed, auto.Run(BackupService.BeforeUpdate, gated: false));
    }
}
