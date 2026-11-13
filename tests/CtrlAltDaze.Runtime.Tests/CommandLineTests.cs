using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Runtime.Tests;

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

    [Theory]
    [InlineData("list")]
    [InlineData("status")]
    [InlineData("history", "tv", "--limit", "2")]
    [InlineData("devices", "audio")]
    [InlineData("check", "network.wifi", "network=Home")]
    [InlineData("flag", "gaming")]
    [InlineData("flag", "gaming", "on")]
    [InlineData("pause")]
    [InlineData("pause", "off")]
    public void JsonIsReadByTheCommandsThatHaveIt(params string[] args)
    {
        Assert.False(Parse(args).Json);
        Assert.True(Parse([.. args, "--json"]).Json);
        Assert.True(Parse([args[0], "--json", .. args[1..]]).Json);
    }

    [Theory]
    [InlineData("run", "tv", "--json")]
    [InlineData("validate", "--json")]
    [InlineData("autostart", "--json")]
    [InlineData("backup", "--json")]
    [InlineData("flag", "--json")]
    [InlineData("flag", "a", "on", "off")]
    [InlineData("pause", "on", "off")]
    [InlineData("flag", "a", "--wait")]
    public void JsonAndWordsAreCheckedStrictly(params string[] args) =>
        Assert.Throws<CommandLineException>(() => Parse(args));

    [Fact]
    public void FlagTakesANameWithSpacesAndAValueThatLooksLikeNameEqualsValue()
    {
        var line = Parse("flag", "my mode=on");

        Assert.Equal("my mode=on", line.Word);
        Assert.Null(line.Inputs);
    }

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

    [Theory]
    [InlineData("stable", "stable")]
    [InlineData("rc", "rc")]
    [InlineData("RC", "rc")]
    public void UpdateTakesAChannel(string given, string expected) =>
        Assert.Equal(expected, Parse("update", "--channel", given).Channel);

    [Fact]
    public void UpdateWithoutAChannelLeavesItUnset() => Assert.Null(Parse("update").Channel);

    [Theory]
    [InlineData("update", "--channel")]
    [InlineData("update", "--channel", "beta")]
    [InlineData("update", "--channel", "prerelease")]
    [InlineData("update", "--channel", "rc", "--channel", "stable")]
    [InlineData("update", "rc")]
    [InlineData("run", "tv", "--channel", "rc")]
    public void BadChannelUseIsAUsageError(params string[] args) =>
        Assert.Throws<CommandLineException>(() => Parse(args));
}
