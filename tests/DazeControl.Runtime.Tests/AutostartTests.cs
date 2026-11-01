using DazeControl.Runtime.Lifecycle;
using Microsoft.Win32;

namespace DazeControl.Runtime.Tests;

public sealed class AutostartTests : IDisposable
{
    private readonly string _root = @"Software\DazeControl.Tests\" + Guid.NewGuid().ToString("N");

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
        Autostart.Enable(@"C:\Apps\Daze Control\DazeControl.exe");

        using var run = Registry.CurrentUser.OpenSubKey(RunKey)!;
        Assert.Equal("\"C:\\Apps\\Daze Control\\DazeControl.exe\"", run.GetValue(Autostart.ValueName));
        Assert.Equal(new AutostartStatus(AutostartState.On, @"C:\Apps\Daze Control\DazeControl.exe"), Autostart.Read());
    }

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

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\DazeControlApp\current\DazeControl.exe", true)]
    [InlineData(@"c:\users\u\appdata\local\dazecontrolapp\current\DazeControl.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Local\DazeControlApp2\current\DazeControl.exe", false)]
    [InlineData(@"C:\src\daze\bin\Debug\DazeControl.exe", false)]
    public void DisableIfUnderOnlyRemovesEntriesInsideTheFolder(string exePath, bool removed)
    {
        Autostart.Enable(exePath);

        Assert.Equal(removed, Autostart.DisableIfUnder(@"C:\Users\u\AppData\Local\DazeControlApp\"));
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
