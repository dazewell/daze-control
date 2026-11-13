using CtrlAltDaze.Runtime.Lifecycle;
using Microsoft.Win32;

namespace CtrlAltDaze.Runtime.Tests;

public sealed class AutostartTests : IDisposable
{
    private readonly string _root = @"Software\CtrlAltDaze.Tests\" + Guid.NewGuid().ToString("N");

    private string RunKey => _root + @"\Run";

    private string ApprovedKey => _root + @"\Approved";

    private Autostart Autostart => new(RunKey, ApprovedKey);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public void MissingKeysReadAsOff() =>
        Assert.Equal(new AutostartStatus(AutostartState.Off, null), Autostart.Read());

    [Fact]
    public void EnableWritesQuotedPath()
    {
        Autostart.Enable(@"C:\Apps\Ctrl Alt Daze\CtrlAltDaze.exe");

        using var run = Registry.CurrentUser.OpenSubKey(RunKey)!;
        Assert.Equal("\"C:\\Apps\\Ctrl Alt Daze\\CtrlAltDaze.exe\"", run.GetValue(Autostart.ValueName));
        Assert.Equal(new AutostartStatus(AutostartState.On, @"C:\Apps\Ctrl Alt Daze\CtrlAltDaze.exe"), Autostart.Read());
    }

    [Fact]
    public void EnableKeepsAConfigArgumentAndReadStillFindsTheExe()
    {
        Autostart.Enable(@"C:\Apps\Ctrl Alt Daze\CtrlAltDaze.exe", @"D:\My Flows\config.yaml");

        using var run = Registry.CurrentUser.OpenSubKey(RunKey)!;
        Assert.Equal("\"C:\\Apps\\Ctrl Alt Daze\\CtrlAltDaze.exe\" \"D:\\My Flows\\config.yaml\"", run.GetValue(Autostart.ValueName));
        Assert.Equal(@"C:\Apps\Ctrl Alt Daze\CtrlAltDaze.exe", Autostart.Read().Path);
    }

    [Theory]
    [InlineData("\"C:\\a b\\x.exe\" \"c.yaml\"", @"C:\a b\x.exe")]
    [InlineData(@"C:\x.exe c.yaml", @"C:\x.exe")]
    [InlineData(@"  C:\x.exe  ", @"C:\x.exe")]
    public void ExePathOfACommand(string command, string exe) => Assert.Equal(exe, Autostart.ExePathOf(command));

    [Theory]
    [InlineData(0x03, AutostartState.DisabledByWindows)]
    [InlineData(0x07, AutostartState.DisabledByWindows)]
    [InlineData(0x02, AutostartState.On)]
    [InlineData(0x06, AutostartState.On)]
    public void TaskManagerFlagIsRespected(byte flag, AutostartState expected)
    {
        Autostart.Enable(@"C:\a.exe");
        SetApproved(flag);

        Assert.Equal(expected, Autostart.Read().State);
    }

    [Fact]
    public void EnableClearsTaskManagerDisabled()
    {
        Autostart.Enable(@"C:\a.exe");
        SetApproved(0x03);

        Autostart.Enable(@"C:\a.exe");

        Assert.Equal(AutostartState.On, Autostart.Read().State);
    }

    [Fact]
    public void DisableRemovesBothValues()
    {
        Autostart.Enable(@"C:\a.exe");
        SetApproved(0x03);

        Autostart.Disable();

        Assert.Equal(AutostartState.Off, Autostart.Read().State);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey)!;
        Assert.Null(approved.GetValue(Autostart.ValueName));
    }

    [Fact]
    public void DisableWithoutKeysDoesNothing() => Autostart.Disable();

    private Autostart Old => new(RunKey, ApprovedKey, "DazeControl");

    private const string OldInstall = @"C:\Users\u\AppData\Local\DazeControlApp";

    private const string NewExe = @"C:\Users\u\AppData\Local\CtrlAltDazeApp\current\CtrlAltDaze.exe";

    [Fact]
    public void CarryFromTurnsTheNewOneOnAndRemovesTheOldOne()
    {
        Old.Enable(OldInstall + @"\current\DazeControl.exe");

        Assert.Equal("setup: Start with Windows carried over from the old app", Autostart.CarryFrom(Old, OldInstall, Autostart, NewExe));

        Assert.Equal(AutostartState.Off, Old.Read().State);
        Assert.Equal(new AutostartStatus(AutostartState.On, NewExe), Autostart.Read());
    }

    [Fact]
    public void CarryFromLeavesItOffWhenWindowsHadTurnedTheOldOneOff()
    {
        Old.Enable(OldInstall + @"\current\DazeControl.exe");
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            approved.SetValue("DazeControl", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);

        Assert.Equal("setup: removed the old app's Start with Windows entry", Autostart.CarryFrom(Old, OldInstall, Autostart, NewExe));

        Assert.Equal(AutostartState.Off, Old.Read().State);
        Assert.Equal(AutostartState.Off, Autostart.Read().State);
    }

    [Fact]
    public void CarryFromIgnoresAnOldEntryOutsideTheOldInstall()
    {
        Old.Enable(@"C:\src\daze\bin\Debug\DazeControl.exe");

        Assert.Null(Autostart.CarryFrom(Old, OldInstall, Autostart, NewExe));

        Assert.Equal(AutostartState.On, Old.Read().State);
        Assert.Equal(AutostartState.Off, Autostart.Read().State);
    }

    [Fact]
    public void CarryFromWithoutAnOldEntryDoesNothing() =>
        Assert.Null(Autostart.CarryFrom(Old, OldInstall, Autostart, NewExe));

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\CtrlAltDazeApp\current\CtrlAltDaze.exe", true)]
    [InlineData(@"c:\users\u\appdata\local\ctrlaltdazeapp\current\CtrlAltDaze.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Local\CtrlAltDazeApp2\current\CtrlAltDaze.exe", false)]
    [InlineData(@"C:\src\daze\bin\Debug\CtrlAltDaze.exe", false)]
    public void DisableIfUnderOnlyRemovesEntriesInsideTheFolder(string exePath, bool removed)
    {
        Autostart.Enable(exePath);

        Assert.Equal(removed, Autostart.DisableIfUnder(@"C:\Users\u\AppData\Local\CtrlAltDazeApp\"));
        Assert.Equal(removed ? AutostartState.Off : AutostartState.On, Autostart.Read().State);
    }

    [Fact]
    public void DisableIfUnderWithoutEntryDoesNothing() => Assert.False(Autostart.DisableIfUnder(@"C:\x"));

    private void SetApproved(byte flag)
    {
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        approved.SetValue(Autostart.ValueName, new byte[] { flag, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }
}
