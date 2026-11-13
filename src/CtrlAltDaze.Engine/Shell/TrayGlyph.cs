using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CtrlAltDaze.Engine.Shell;

internal enum TrayGlyphKind
{
    Idle,
    Busy,
    Paused,
}

/// <summary>
/// The tray icon, drawn in code at the small-icon size so the AOT engine ships no image files (UX §5.6 variants):
/// the app mark (scripts/make-icon.ps1: a step fanning out to two that join again, on the orange keycap tile, D-153), all
/// white here; busy adds a charcoal dot; paused is a grey tile with pause bars. Reads on light and dark taskbars
/// alike. 4×4 supersampling for the edges.
/// </summary>
internal static unsafe class TrayGlyph
{
    private const uint Orange = 0xFFFF8A3D;
    private const uint DeepOrange = 0xFFF25C24;
    private const uint Grey = 0xFF6E6E6E;
    private const uint White = 0xFFFFFFFF;
    private const uint Charcoal = 0xFF24292F;
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
    internal static uint[] Paint(TrayGlyphKind kind, int size) => Paint(size, (u, v) => Sample(kind, u, v));

    /// <summary>The app tile alone (orange, rounded), for a desktop shortcut's icon under a flow's glyph (D-049).</summary>
    internal static uint[] PaintTile(int size) =>
        Paint(size, (u, v) => InRoundedSquare(u, v, inset: 0.04, radius: 0.24) ? Lerp(Orange, DeepOrange, Math.Clamp((u + v) / 2, 0, 1)) : 0);

    private static uint[] Paint(int size, Func<double, double, uint> sample)
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
                        var c = sample(u, v);
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
                return Charcoal;
            if (d <= 0.23)
                return White;
        }
        if (kind == TrayGlyphKind.Paused)
        {
            if (v is >= 0.3 and <= 0.7 && (u is >= 0.33 and <= 0.45 || u is >= 0.55 and <= 0.67))
                return White;
        }
        else if (InFlowMark(u, v))
        {
            return White;
        }
        if (!InRoundedSquare(u, v, inset: 0.04, radius: 0.24))
            return 0;
        return kind == TrayGlyphKind.Paused ? Grey : Lerp(Orange, DeepOrange, Math.Clamp((u + v) / 2, 0, 1));
    }

    // The mark in unit space, as make-icon.ps1 draws it on its 16 grid: steps of radius 1.7, edges 1.2 wide.
    private const double StepRadius = 1.7 / 16;
    private const double EdgeHalfWidth = 0.6 / 16;
    private static readonly (double U, double V)[] Steps = [(3.2 / 16, 0.5), (0.5, 4.3 / 16), (0.5, 11.7 / 16), (12.8 / 16, 0.5)];
    private static readonly (double U, double V)[] EdgePoints = BuildEdges();

    private static bool InFlowMark(double u, double v)
    {
        foreach (var (su, sv) in Steps)
        {
            if ((u - su) * (u - su) + (v - sv) * (v - sv) <= StepRadius * StepRadius)
                return true;
        }
        for (var i = 0; i + 1 < EdgePoints.Length; i++)
        {
            if (i % (EdgeSegments + 1) == EdgeSegments)
                continue; // gap between two edges' polylines
            if (DistanceToSegment(u, v, EdgePoints[i], EdgePoints[i + 1]) <= EdgeHalfWidth)
                return true;
        }
        return false;
    }

    private const int EdgeSegments = 16;

    /// <summary>The four horizontal Béziers (start → both, both → done) as polylines, back to back.</summary>
    private static (double, double)[] BuildEdges()
    {
        var edges = new[] { (0, 1), (0, 2), (1, 3), (2, 3) };
        var points = new List<(double, double)>();
        foreach (var (from, to) in edges)
        {
            var (x0, y0) = Steps[from];
            var (x1, y1) = Steps[to];
            var mid = (x0 + x1) / 2;
            for (var i = 0; i <= EdgeSegments; i++)
            {
                var t = (double)i / EdgeSegments;
                var a = (1 - t) * (1 - t) * (1 - t);
                var b = 3 * (1 - t) * (1 - t) * t;
                var c = 3 * (1 - t) * t * t;
                var d = t * t * t;
                points.Add((a * x0 + b * mid + c * mid + d * x1, a * y0 + b * y0 + c * y1 + d * y1));
            }
        }
        return [.. points];
    }

    private static double DistanceToSegment(double u, double v, (double U, double V) p, (double U, double V) q)
    {
        var du = q.U - p.U;
        var dv = q.V - p.V;
        var lengthSquared = du * du + dv * dv;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((u - p.U) * du + (v - p.V) * dv) / lengthSquared, 0, 1);
        var eu = u - (p.U + t * du);
        var ev = v - (p.V + t * dv);
        return Math.Sqrt(eu * eu + ev * ev);
    }

    private static uint Lerp(uint from, uint to, double t)
    {
        static uint Channel(uint c, int shift) => (c >> shift) & 0xFF;
        uint Mix(int shift) => (uint)Math.Round(Channel(from, shift) + (Channel(to, shift) - (double)Channel(from, shift)) * t);
        return 0xFF000000 | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
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
