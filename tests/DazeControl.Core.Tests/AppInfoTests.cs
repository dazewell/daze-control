using DazeControl.Core;

namespace DazeControl.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void FolderNameHasNoSpaces() => Assert.DoesNotContain(' ', AppInfo.FolderName);
}
