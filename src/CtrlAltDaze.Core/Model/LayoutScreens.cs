using System.Buffers.Binary;
using System.Globalization;

namespace CtrlAltDaze.Core.Model;

/// <summary>
/// One screen of a captured <see cref="DisplayLayout"/>, read back from its CCD bytes for the Library's details.
/// <paramref name="Connection"/> is the cable type ("HDMI", "DP"); the port number isn't in the capture.
/// </summary>
public sealed record LayoutScreen(string Monitor, string Connection, int Width, int Height, int X, int Y, double RefreshHz)
{
    /// <summary>Windows puts the primary screen's top-left corner at (0, 0).</summary>
    public bool Primary => X == 0 && Y == 0;

    /// <summary>"DELA1F2" from <c>\\?\DISPLAY#DELA1F2#5&amp;…</c>: the maker and model code, for a screen that isn't plugged in.</summary>
    public string HardwareId => Monitor.Split('#') is { Length: > 2 } parts && parts[1].Length > 0 ? parts[1] : Monitor;

    public string Resolution => string.Create(CultureInfo.InvariantCulture, $"{Width} × {Height}");

    public string Position => string.Create(CultureInfo.InvariantCulture, $"{X}, {Y}");

    /// <summary>"60 Hz", "59.94 Hz"; "" when the capture doesn't say.</summary>
    public string Refresh => RefreshHz > 0 ? RefreshHz.ToString("0.##", CultureInfo.InvariantCulture) + " Hz" : "";
}

public static class LayoutScreens
{
    // Offsets in DISPLAYCONFIG_PATH_INFO (72 bytes) and DISPLAYCONFIG_MODE_INFO (64 bytes) on x64, as CcdLayout saves them.
    private const int SourceModeIndex = 12, TargetModeIndex = 32, Technology = 36, RefreshNumerator = 48, RefreshDenominator = 52;
    private const int ModeType = 0, SourceWidth = 16, SourceHeight = 20, SourceX = 28, SourceY = 32, VSyncNumerator = 32, VSyncDenominator = 36;
    private const int SourceMode = 1, TargetMode = 2;

    /// <summary>The screens in capture order (one per monitor); empty for a damaged layout.</summary>
    public static IReadOnlyList<LayoutScreen> Read(DisplayLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.IsWellFormed())
            return [];
        var bytes = Convert.FromBase64String(layout.Data).AsSpan();
        var pathCount = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var modeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(int)..]);
        var paths = bytes.Slice(sizeof(int) * 2, pathCount * DisplayLayout.PathSize);
        var modes = bytes.Slice(sizeof(int) * 2 + paths.Length, modeCount * DisplayLayout.ModeSize);

        var screens = new List<LayoutScreen>(pathCount);
        for (var i = 0; i < pathCount; i++)
        {
            var path = paths.Slice(i * DisplayLayout.PathSize, DisplayLayout.PathSize);
            var source = Mode(modes, modeCount, U32(path, SourceModeIndex), SourceMode);
            var target = Mode(modes, modeCount, U32(path, TargetModeIndex), TargetMode);
            var refresh = Rate(path, RefreshNumerator, RefreshDenominator);
            if (refresh == 0 && !target.IsEmpty)
                refresh = Rate(target, VSyncNumerator, VSyncDenominator);
            screens.Add(new LayoutScreen(layout.Monitors[i], Connection(U32(path, Technology)),
                source.IsEmpty ? 0 : (int)U32(source, SourceWidth), source.IsEmpty ? 0 : (int)U32(source, SourceHeight),
                source.IsEmpty ? 0 : I32(source, SourceX), source.IsEmpty ? 0 : I32(source, SourceY), refresh));
        }
        return screens;
    }

    private static ReadOnlySpan<byte> Mode(ReadOnlySpan<byte> modes, int count, uint index, int type)
    {
        if (index >= count) // DISPLAYCONFIG_PATH_MODE_IDX_INVALID is 0xffffffff
            return [];
        var mode = modes.Slice((int)index * DisplayLayout.ModeSize, DisplayLayout.ModeSize);
        return I32(mode, ModeType) == type ? mode : [];
    }

    private static double Rate(ReadOnlySpan<byte> span, int numerator, int denominator) =>
        U32(span, denominator) is > 0 and var d ? Math.Round((double)U32(span, numerator) / d, 2) : 0;

    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);

    private static int I32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY, in words (the live ports add a number: "HDMI-1", CcdDisplays.Prefix).
    private static string Connection(uint technology) => technology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 => "DP",
        11 => "eDP",
        15 => "Miracast",
        0x80000000 => "Built-in",
        _ => "Other",
    };
}
