using CtrlAltDaze.Core;

namespace CtrlAltDaze.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void FolderNameHasNoSpaces() => Assert.DoesNotContain(' ', AppInfo.FolderName);
}
