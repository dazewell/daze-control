using CtrlAltDaze.Runtime.Diagnostics;

namespace CtrlAltDaze.Runtime.Tests;

public sealed class LogRotationTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 0, 0, 5);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests", Guid.NewGuid().ToString("N"));

    public LogRotationTests() => Directory.CreateDirectory(_directory);

    private string Current => Path.Combine(_directory, LogRotation.FileName);

    private string Archive(int month, int day) => Path.Combine(_directory, LogRotation.ArchiveName(new DateTime(2026, month, day)));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteCurrent(string text, DateTime lastWrite)
    {
        File.WriteAllText(Current, text);
        File.SetLastWriteTime(Current, lastWrite);
    }

    [Fact]
    public void YesterdaysLogIsArchivedUnderItsDay()
    {
        WriteCurrent("yesterday\n", new DateTime(2026, 10, 22, 23, 59, 0));

        LogRotation.Rotate(_directory, Now);

        Assert.False(File.Exists(Current));
        Assert.Equal("yesterday\n", File.ReadAllText(Archive(10, 22)));
    }

    [Fact]
    public void TodaysLogStays()
    {
        WriteCurrent("today\n", new DateTime(2026, 10, 23, 0, 0, 1));

        LogRotation.Rotate(_directory, Now);

        Assert.Equal("today\n", File.ReadAllText(Current));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ALogLastWrittenDaysAgoIsArchivedUnderThatDay()
    {
        WriteCurrent("old\n", new DateTime(2026, 10, 19, 12, 0, 0));

        LogRotation.Rotate(_directory, Now);

        Assert.Equal("old\n", File.ReadAllText(Archive(10, 19)));
    }

    [Fact]
    public void KeepsSevenDaysTodayIncluded()
    {
        File.WriteAllText(Archive(10, 16), "");
        File.WriteAllText(Archive(10, 17), "");
        File.WriteAllText(Archive(10, 22), "");

        LogRotation.Rotate(_directory, Now);

        Assert.False(File.Exists(Archive(10, 16)));
        Assert.True(File.Exists(Archive(10, 17)));
        Assert.True(File.Exists(Archive(10, 22)));
    }

    [Fact]
    public void AnExistingArchiveOfThatDayIsAppendedTo()
    {
        File.WriteAllText(Archive(10, 22), "first\n");
        WriteCurrent("second\n", new DateTime(2026, 10, 22, 20, 0, 0));

        LogRotation.Rotate(_directory, Now);

        Assert.False(File.Exists(Current));
        Assert.Equal("first\nsecond\n", File.ReadAllText(Archive(10, 22)));
    }

    [Fact]
    public void OtherFilesAreLeftAlone()
    {
        var notes = Path.Combine(_directory, "engine-notes.log");
        var settings = Path.Combine(_directory, "settings.json");
        File.WriteAllText(notes, "");
        File.WriteAllText(settings, "");
        File.SetLastWriteTime(notes, new DateTime(2020, 1, 1));

        LogRotation.Rotate(_directory, Now);

        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(settings));
    }

    [Fact]
    public void ALockedLogIsLeftInPlace()
    {
        WriteCurrent("yesterday\n", new DateTime(2026, 10, 22, 23, 0, 0));
        using (new FileStream(Current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            LogRotation.Rotate(_directory, Now);

        Assert.Equal("yesterday\n", File.ReadAllText(Current));
        Assert.False(File.Exists(Archive(10, 22)));
    }

    [Fact]
    public void ALockedLogIsNotAppendedTwiceToAnExistingArchive()
    {
        File.WriteAllText(Archive(10, 22), "first\n");
        WriteCurrent("second\n", new DateTime(2026, 10, 22, 20, 0, 0));
        using (new FileStream(Current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            LogRotation.Rotate(_directory, Now);

        LogRotation.Rotate(_directory, Now);

        Assert.False(File.Exists(Current));
        Assert.Equal("first\nsecond\n", File.ReadAllText(Archive(10, 22)));
    }

    [Fact]
    public void ALockedArchivePutsTheLogBack()
    {
        File.WriteAllText(Archive(10, 22), "first\n");
        WriteCurrent("second\n", new DateTime(2026, 10, 22, 20, 0, 0));
        using (new FileStream(Archive(10, 22), FileMode.Open, FileAccess.Read, FileShare.None))
            LogRotation.Rotate(_directory, Now);

        Assert.Equal("second\n", File.ReadAllText(Current));
        Assert.False(File.Exists(Current + ".rotating"));

        LogRotation.Rotate(_directory, Now);

        Assert.Equal("first\nsecond\n", File.ReadAllText(Archive(10, 22)));
    }

    [Fact]
    public void ALeftoverRotatingFileIsRecoveredIntoItsDay()
    {
        var moved = Current + ".rotating";
        File.WriteAllText(Archive(10, 21), "first\n");
        File.WriteAllText(moved, "cut\n");
        File.SetLastWriteTime(moved, new DateTime(2026, 10, 21, 22, 0, 0));
        WriteCurrent("today\n", new DateTime(2026, 10, 23, 0, 0, 1));

        LogRotation.Rotate(_directory, Now);

        Assert.False(File.Exists(moved));
        Assert.Equal("first\ncut\n", File.ReadAllText(Archive(10, 21)));
        Assert.Equal("today\n", File.ReadAllText(Current));
    }

    [Fact]
    public void AnUnrecoveredRotatingFileIsNeverOverwritten()
    {
        var moved = Current + ".rotating";
        File.WriteAllText(moved, "cut\n");
        File.SetLastWriteTime(moved, new DateTime(2026, 10, 22, 22, 0, 0));
        File.WriteAllText(Archive(10, 22), "first\n");
        WriteCurrent("second\n", new DateTime(2026, 10, 22, 23, 0, 0));
        using (new FileStream(Archive(10, 22), FileMode.Open, FileAccess.Read, FileShare.None))
            LogRotation.Rotate(_directory, Now);

        Assert.Equal("cut\n", File.ReadAllText(moved));
        Assert.Equal("second\n", File.ReadAllText(Current));
    }
}
