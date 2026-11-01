using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

internal enum TrayGlyphKind
{
    Idle,
    Busy,
    Paused,
}

/// <summary>
/// The tray icon, drawn in code at the small-icon size so the AOT engine ships no image files (UX §5.6 variants):
/// an accent rounded square with a play mark; busy adds an amber dot; paused is grey with pause bars. Reads on
/// light and dark taskbars alike. 4×4 supersampling for the edges.
/// </summary>
internal static unsafe class TrayGlyph
{
    private const uint Accent = 0xFF0078D4;
    private const uint Grey = 0xFF6E6E6E;
    private const uint White = 0xFFFFFFFF;
    private const uint Amber = 0xFFFFB900;
    private const int Samples = 4;

    /// <summary>A new icon handle; the caller destroys it (<c>DestroyIcon</c>).</summary>
    public static HICON Create(TrayGlyphKind kind)
    {
        var size = Math.Max(16, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON));
        var pixels = Paint(kind, size);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        void* bits;
        var color = PInvoke.CreateDIBSection(HDC.Null, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (color.IsNull)
            return HICON.Null;
        var mask = HBITMAP.Null;
        try
        {
            pixels.AsSpan().CopyTo(new Span<uint>(bits, pixels.Length));
            var zeros = new byte[(size + 15) / 16 * 2 * size]; // 1 bpp, rows WORD-aligned; alpha decides
            fixed (byte* maskBits = zeros)
                mask = PInvoke.CreateBitmap(size, size, 1, 1, maskBits);
            var icon = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return PInvoke.CreateIconIndirect(&icon);
        }
        finally
        {
            PInvoke.DeleteObject(color);
            if (!mask.IsNull)
                PInvoke.DeleteObject(mask);
        }
    }

    /// <summary>Straight-alpha ARGB, row-major, top-down.</summary>
    internal static uint[] Paint(TrayGlyphKind kind, int size)
    {
        var pixels = new uint[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double a = 0, r = 0, g = 0, b = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var u = (x + (sx + 0.5) / Samples) / size;
                        var v = (y + (sy + 0.5) / Samples) / size;
                        var c = Sample(kind, u, v);
                        var alpha = (c >> 24) / 255.0;
                        a += alpha;
                        r += alpha * ((c >> 16) & 0xFF);
                        g += alpha * ((c >> 8) & 0xFF);
                        b += alpha * (c & 0xFF);
                    }
                }
                if (a == 0)
                    continue;
                pixels[y * size + x] = ((uint)Math.Round(a / (Samples * Samples) * 255) << 24)
                    | ((uint)Math.Round(r / a) << 16) | ((uint)Math.Round(g / a) << 8) | (uint)Math.Round(b / a);
            }
        }
        return pixels;
    }

    /// <summary>The colour at (u, v) in the unit square, topmost shape first.</summary>
    private static uint Sample(TrayGlyphKind kind, double u, double v)
    {
        if (kind == TrayGlyphKind.Busy)
        {
            var d = Math.Sqrt((u - 0.78) * (u - 0.78) + (v - 0.78) * (v - 0.78));
            if (d <= 0.17)
                return Amber;
            if (d <= 0.23)
                return White;
        }
        if (kind == TrayGlyphKind.Paused)
        {
            if (v is >= 0.3 and <= 0.7 && (u is >= 0.33 and <= 0.45 || u is >= 0.55 and <= 0.67))
                return White;
        }
        else if (InTriangle(u, v))
        {
            return White;
        }
        return InRoundedSquare(u, v, inset: 0.04, radius: 0.24) ? (kind == TrayGlyphKind.Paused ? Grey : Accent) : 0;
    }

    private static bool InTriangle(double u, double v)
    {
        // Play mark: (0.38, 0.28) – (0.38, 0.72) – (0.74, 0.5).
        if (u < 0.38)
            return false;
        var halfHeight = 0.22 * (0.74 - u) / 0.36;
        return Math.Abs(v - 0.5) <= halfHeight;
    }

    private static bool InRoundedSquare(double u, double v, double inset, double radius)
    {
        var lo = inset + radius;
        var hi = 1 - inset - radius;
        var dx = Math.Max(0, Math.Max(lo - u, u - hi));
        var dy = Math.Max(0, Math.Max(lo - v, v - hi));
        return u >= inset && u <= 1 - inset && v >= inset && v <= 1 - inset && dx * dx + dy * dy <= radius * radius;
    }
}
