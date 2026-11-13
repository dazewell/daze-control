using System.Text.Json;
using System.Text.Json.Serialization;

namespace CtrlAltDaze.Editor.ViewModels.Config;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>One monitor's work area (screen pixels, taskbar excluded) and its DPI.</summary>
public readonly record struct MonitorArea(PixelRect Work, int Dpi);

/// <summary>Where the editor window was last closed (D-098): the restored rectangle, not the maximized one.</summary>
public sealed class WindowSettings
{
    /// <summary>The normal (restored) rectangle in physical screen pixels.</summary>
    public int? Left { get; set; }

    public int? Top { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public bool Maximized { get; set; }

    /// <summary>The window's DPI when saved, so a different monitor scale keeps the size in DIP.</summary>
    public int? Dpi { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }
}

/// <summary>Where the window opens: <see cref="Bounds"/> on screen, <see cref="Maximized"/> on top of it.</summary>
public readonly record struct WindowTarget(PixelRect Bounds, bool Maximized);

/// <summary>Turns the saved window rectangle into one that is safe to show on the monitors that exist now (D-098).</summary>
public static class WindowPlacementLogic
{
    public const int DefaultWidthDip = 1100;
    public const int DefaultHeightDip = 720;

    /// <summary>Smaller than this (in DIP) is treated as a bad file, not a choice.</summary>
    public const int MinWidthDip = 640;
    public const int MinHeightDip = 420;

    /// <summary>At least this much (px) of the window must lie on a monitor, so its title bar can be grabbed.</summary>
    public const int MinVisibleWidth = 120;
    public const int MinVisibleHeight = 48;

    /// <summary>
    /// <paramref name="saved"/> null or unusable → the default size centred on <paramref name="current"/>.
    /// Otherwise the saved rectangle, scaled if its monitor's DPI differs, clamped into the monitor it overlaps most;
    /// a rectangle on no monitor (unplugged, resolution change) → the default.
    /// </summary>
    public static WindowTarget Resolve(WindowSettings? saved, IReadOnlyList<MonitorArea> monitors, MonitorArea current)
    {
        var maximized = saved?.Maximized ?? false;
        if (saved is not { Left: { } left, Top: { } top, Width: { } width, Height: { } height } || monitors.Count == 0)
            return new WindowTarget(Default(current), maximized);

        var savedDpi = saved.Dpi is > 0 ? saved.Dpi.Value : 96;
        if (width is <= 0 or > 100_000 || height is <= 0 or > 100_000
            || width * 96L / savedDpi < MinWidthDip || height * 96L / savedDpi < MinHeightDip)
            return new WindowTarget(Default(current), maximized);

        var rect = new PixelRect(left, top, width, height);
        var best = default(MonitorArea);
        long bestArea = 0;
        foreach (var monitor in monitors)
        {
            var overlap = Overlap(rect, monitor.Work);
            if (overlap.Width < MinVisibleWidth || overlap.Height < MinVisibleHeight)
                continue;
            if ((long)overlap.Width * overlap.Height > bestArea)
            {
                best = monitor;
                bestArea = (long)overlap.Width * overlap.Height;
            }
        }
        if (bestArea == 0)
            return new WindowTarget(Default(current), maximized);

        if (best.Dpi > 0 && best.Dpi != savedDpi)
        {
            width = (int)Math.Round(width * (double)best.Dpi / savedDpi);
            height = (int)Math.Round(height * (double)best.Dpi / savedDpi);
        }
        return new WindowTarget(Clamp(new PixelRect(left, top, width, height), best.Work), maximized);
    }

    /// <summary>The first-run size (<see cref="DefaultWidthDip"/> × <see cref="DefaultHeightDip"/> DIP) centred on <paramref name="area"/>.</summary>
    public static PixelRect Default(MonitorArea area)
    {
        var dpi = area.Dpi > 0 ? area.Dpi : 96;
        var w = Math.Min(DefaultWidthDip * dpi / 96, area.Work.Width);
        var h = Math.Min(DefaultHeightDip * dpi / 96, area.Work.Height);
        return new PixelRect(area.Work.Left + (area.Work.Width - w) / 2, area.Work.Top + (area.Work.Height - h) / 2, w, h);
    }

    /// <summary>Shrinks to the work area if larger, then slides inside it.</summary>
    public static PixelRect Clamp(PixelRect rect, PixelRect work)
    {
        var w = Math.Min(rect.Width, work.Width);
        var h = Math.Min(rect.Height, work.Height);
        var l = Math.Clamp(rect.Left, work.Left, work.Right - w);
        var t = Math.Clamp(rect.Top, work.Top, work.Bottom - h);
        return new PixelRect(l, t, w, h);
    }

    private static PixelRect Overlap(PixelRect a, PixelRect b)
    {
        var l = Math.Max(a.Left, b.Left);
        var t = Math.Max(a.Top, b.Top);
        var r = Math.Min(a.Right, b.Right);
        var bt = Math.Min(a.Bottom, b.Bottom);
        return new PixelRect(l, t, Math.Max(0, r - l), Math.Max(0, bt - t));
    }
}
