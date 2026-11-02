using System.Buffers.Binary;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class LayoutScreensTests
{
    private const string Dell = @"\?\DISPLAY#DELA1F2#5&1a2b3c&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Lg = @"\?\DISPLAY#GSM5B7F#5&1a2b3c&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>The bytes CcdLayout saves, with only the fields the details read filled in.</summary>
    private static string Capture(params (uint Source, uint Target, uint Technology, uint RefreshNum, uint RefreshDen)[] paths)
    {
        var modes = new List<byte[]>
        {
            SourceMode(2560, 1440, 0, 0),
            TargetMode(60000, 1000),
            SourceMode(1920, 1080, 2560, -200),
            TargetMode(143856, 1000),
        };
        var bytes = new byte[8 + paths.Length * DisplayLayout.PathSize + modes.Count * DisplayLayout.ModeSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, paths.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), modes.Count);
        for (var i = 0; i < paths.Length; i++)
        {
            var path = bytes.AsSpan(8 + i * DisplayLayout.PathSize);
            BinaryPrimitives.WriteUInt32LittleEndian(path[12..], paths[i].Source);
            BinaryPrimitives.WriteUInt32LittleEndian(path[32..], paths[i].Target);
            BinaryPrimitives.WriteUInt32LittleEndian(path[36..], paths[i].Technology);
            BinaryPrimitives.WriteUInt32LittleEndian(path[48..], paths[i].RefreshNum);
            BinaryPrimitives.WriteUInt32LittleEndian(path[52..], paths[i].RefreshDen);
        }
        for (var i = 0; i < modes.Count; i++)
            modes[i].CopyTo(bytes, 8 + paths.Length * DisplayLayout.PathSize + i * DisplayLayout.ModeSize);
        return Convert.ToBase64String(bytes);
    }

    private static byte[] SourceMode(int width, int height, int x, int y)
    {
        var mode = new byte[DisplayLayout.ModeSize];
        BinaryPrimitives.WriteInt32LittleEndian(mode, 1);
        BinaryPrimitives.WriteInt32LittleEndian(mode.AsSpan(16), width);
        BinaryPrimitives.WriteInt32LittleEndian(mode.AsSpan(20), height);
        BinaryPrimitives.WriteInt32LittleEndian(mode.AsSpan(28), x);
        BinaryPrimitives.WriteInt32LittleEndian(mode.AsSpan(32), y);
        return mode;
    }

    private static byte[] TargetMode(uint vSyncNum, uint vSyncDen)
    {
        var mode = new byte[DisplayLayout.ModeSize];
        BinaryPrimitives.WriteInt32LittleEndian(mode, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(32), vSyncNum);
        BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(36), vSyncDen);
        return mode;
    }

    [Fact]
    public void ReadsEachScreensModeFromTheCapture()
    {
        var layout = new DisplayLayout("desk", [Dell, Lg], Capture((0, 1, 5, 60000, 1000), (2, 3, 10, 0, 0)));

        var screens = LayoutScreens.Read(layout);

        Assert.Equal(2, screens.Count);
        var (dell, lg) = (screens[0], screens[1]);
        Assert.Equal((Dell, "HDMI", "2560 × 1440", "0, 0", "60 Hz", true, "DELA1F2"),
            (dell.Monitor, dell.Connection, dell.Resolution, dell.Position, dell.Refresh, dell.Primary, dell.HardwareId));
        // No refresh on the path: the target mode's vertical sync says it.
        Assert.Equal(("DP", "1920 × 1080", "2560, -200", "143.86 Hz", false, "GSM5B7F"),
            (lg.Connection, lg.Resolution, lg.Position, lg.Refresh, lg.Primary, lg.HardwareId));
    }

    [Fact]
    public void AModeIndexOutOfRangeLeavesThoseFieldsEmpty()
    {
        var screen = Assert.Single(LayoutScreens.Read(new DisplayLayout("d", [Dell], Capture((0xffffffff, 0xffffffff, 0x80000000, 0, 0)))));

        Assert.Equal(("Built-in", 0, 0, ""), (screen.Connection, screen.Width, screen.Height, screen.Refresh));
    }

    [Fact]
    public void ADamagedLayoutHasNoScreens() =>
        Assert.Empty(LayoutScreens.Read(new DisplayLayout("d", [Dell, Lg], Capture((0, 1, 5, 60, 1)))));
}
