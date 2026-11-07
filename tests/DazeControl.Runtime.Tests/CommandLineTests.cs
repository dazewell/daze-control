using DazeControl.Ipc;

namespace DazeControl.Runtime.Tests;

public class CommandLineTests
{
    private static CommandLine Parse(params string[] args) => CommandLine.Parse(args[0], args[1..]);

    [Theory]
    [InlineData("run", "tv", "--dryrun")]
    [InlineData("run", "tv", "--limit", "3")]
    [InlineData("history", "--wait")]
    [InlineData("status", "-v")]
    [InlineData("run", "tv", "--remove")]
    public void UnknownFlagIsAUsageError(params string[] args) =>
        Assert.Throws<CommandLineException>(() => Parse(args));

    [Theory]
    [InlineData("validate", "bad.yaml", "extra")]
    [InlineData("run")]
    [InlineData("run", "a", "b")]
    [InlineData("cancel")]
    [InlineData("list", "x")]
    [InlineData("status", "x")]
    [InlineData("history", "a", "b")]
    [InlineData("autostart", "on", "off")]
    [InlineData("devices")]
    [InlineData("capture-display-layout")]
    [InlineData("capture-display-layout", "a", "b")]
    [InlineData("shortcut")]
    [InlineData("shortcut", "a", "b")]
    [InlineData("nope")]
    public void WrongWordCountIsAUsageError(params string[] args) =>
        Assert.Throws<CommandLineException>(() => Parse(args));

    [Theory]
    [InlineData("history", "--limit")]
    [InlineData("history", "--limit", "0")]
    [InlineData("history", "--limit", "x")]
    [InlineData("history", "--limit", "-3")]
    [InlineData("history", "--limit", "2", "--limit", "3")]
    public void BadLimitIsAUsageError(params string[] args) =>
        Assert.Throws<CommandLineException>(() => Parse(args));

    [Fact]
    public void RunReadsItsFlags()
    {
        var line = Parse("run", "--dry-run", "tv", "--wait");
        Assert.Equal("tv", line.Word);
        Assert.True(line.DryRun);
        Assert.True(line.Wait);
    }

    [Fact]
    public void ShortcutReadsRemove()
    {
        Assert.False(Parse("shortcut", "tv").Remove);
        var line = Parse("shortcut", "--remove", "tv");
        Assert.Equal("tv", line.Word);
        Assert.True(line.Remove);
    }

    [Fact]
    public void HistoryLimitIsNotTheFlow()
    {
        var line = Parse("history", "--limit", "5", "tv");
        Assert.Equal(["tv"], line.Words);
        Assert.Equal(5, line.Limit);
    }

    [Fact]
    public void ValidateTakesOneOptionalFile()
    {
        Assert.Null(Parse("validate").Word);
        Assert.Equal("a.yaml", Parse("validate", "a.yaml").Word);
    }

    [Fact]
    public void RestoreTakesItsFlags()
    {
        var line = Parse("restore", "a.dcbackup", "--merge", "--yes", "--editor", "--config", "c.yaml");
        Assert.Equal("a.dcbackup", line.Word);
        Assert.True(line.Merge && line.Yes && line.Editor);
        Assert.Equal("c.yaml", line.Config);
        Assert.True(Parse("restore", "a", "--dry-run").DryRun);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("restore", "a", "--merge", "--replace")]
    [InlineData("restore", "a", "--config")]
    [InlineData("restore", "a", "--history")]
    [InlineData("backup", "a", "b")]
    [InlineData("backup", "--yes")]
    [InlineData("run", "tv", "--config", "x")]
    public void BackupAndRestoreAreStrict(params string[] args) =>
        Assert.Throws<CommandLineException>(() => Parse(args));
}
