namespace CtrlAltDaze.Core.Tests;

/// <summary>T29.2 (D-152): the old folders are copied once, without overwriting, config last.</summary>
public sealed class OldSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctrlaltdaze-oldsetup-" + Guid.NewGuid().ToString("N"));

    private string Roaming => Path.Combine(_root, "Roaming");

    private string Local => Path.Combine(_root, "Local");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void CopiesBothFoldersAndLeavesTheOldOnes()
    {
        Put(Path.Combine(Roaming, "DazeControl", "config.yaml"), "flows: {}");
        Put(Path.Combine(Roaming, "DazeControl", "backups", "a.yaml"), "a");
        Put(Path.Combine(Local, "DazeControl", "history.jsonl"), "h");
        Put(Path.Combine(Local, "DazeControl", "shortcuts", "tv.ico"), "i");

        Assert.Equal("setup: copied 4 files from the DazeControl folders", OldSetup.CopyFolders(Roaming, Local));

        Assert.Equal("flows: {}", File.ReadAllText(Path.Combine(Roaming, "CtrlAltDaze", "config.yaml")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(Roaming, "CtrlAltDaze", "backups", "a.yaml")));
        Assert.Equal("h", File.ReadAllText(Path.Combine(Local, "CtrlAltDaze", "history.jsonl")));
        Assert.Equal("i", File.ReadAllText(Path.Combine(Local, "CtrlAltDaze", "shortcuts", "tv.ico")));
        Assert.True(File.Exists(Path.Combine(Roaming, "DazeControl", "config.yaml")));
    }

    [Fact]
    public void ANewConfigMeansNothingIsCopied()
    {
        Put(Path.Combine(Roaming, "DazeControl", "config.yaml"), "old");
        Put(Path.Combine(Roaming, "CtrlAltDaze", "config.yaml"), "new");

        Assert.Null(OldSetup.CopyFolders(Roaming, Local));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Roaming, "CtrlAltDaze", "config.yaml")));
    }

    [Fact]
    public void NoOldConfigMeansNothingIsCopied()
    {
        Put(Path.Combine(Local, "DazeControl", "history.jsonl"), "h");

        Assert.Null(OldSetup.CopyFolders(Roaming, Local));
        Assert.False(Directory.Exists(Path.Combine(Local, "CtrlAltDaze")));
    }

    [Fact]
    public void FilesAlreadyThereStayAndAStoppedCopyFinishesLater()
    {
        Put(Path.Combine(Roaming, "DazeControl", "config.yaml"), "flows: {}");
        Put(Path.Combine(Local, "DazeControl", "history.jsonl"), "old");
        Put(Path.Combine(Local, "DazeControl", "logs", "engine.log"), "log");
        Put(Path.Combine(Local, "CtrlAltDaze", "history.jsonl"), "new");

        Assert.Equal("setup: copied 2 files from the DazeControl folders", OldSetup.CopyFolders(Roaming, Local));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Local, "CtrlAltDaze", "history.jsonl")));
    }

    [Fact]
    public void AFailedCopyIsReportedNotThrownAndLeavesNoNewConfig()
    {
        Put(Path.Combine(Roaming, "DazeControl", "config.yaml"), "flows: {}");
        Put(Path.Combine(Local, "DazeControl", "history.jsonl"), "h");
        using var locked = new FileStream(Path.Combine(Local, "DazeControl", "history.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.StartsWith("setup: copying from the DazeControl folders stopped:", OldSetup.CopyFolders(Roaming, Local), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Roaming, "CtrlAltDaze", "config.yaml")));
    }
}
