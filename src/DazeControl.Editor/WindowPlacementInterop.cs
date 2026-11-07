using System.Runtime.InteropServices;
using DazeControl.Editor.ViewModels.Config;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DazeControl.Editor;

/// <summary>Reads and applies the editor window's placement (D-098). Win32 pieces only; the rules live in <see cref="WindowPlacementLogic"/>.</summary>
internal static partial class WindowPlacementInterop
{
    private const uint ShowMaximize = 3;
    private const uint RestoreToMaximized = 2; // WPF_RESTORETOMAXIMIZED
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public uint Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public NativeRect Normal;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint hwnd, ref Placement placement);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    /// <summary>The restored rectangle (workspace → screen pixels), the maximized flag and the window's DPI. Null if the call fails.</summary>
    public static WindowSettings? Capture(Window window)
    {
        try
        {
            var hwnd = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
            var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(hwnd, ref placement))
                return null;
            // rcNormalPosition is in workspace coordinates: relative to the primary work area, not the screen.
            var primary = DisplayArea.Primary;
            var dx = primary.WorkArea.X - primary.OuterBounds.X;
            var dy = primary.WorkArea.Y - primary.OuterBounds.Y;
            var n = placement.Normal;
            var dpi = (int)GetDpiForWindow(hwnd);
            return new WindowSettings
            {
                Left = n.Left + dx,
                Top = n.Top + dy,
                Width = n.Right - n.Left,
                Height = n.Bottom - n.Top,
                Maximized = placement.ShowCmd == ShowMaximize || (placement.Flags & RestoreToMaximized) != 0, // a minimized window keeps the flag
                Dpi = dpi > 0 ? dpi : null,
            };
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Moves and sizes the window per the saved state, before it is first shown; maximizes after if it was.</summary>
    public static void Apply(Window window, WindowSettings? saved)
    {
        var areas = DisplayArea.FindAll();
        var monitors = new List<MonitorArea>();
        for (var i = 0; i < areas.Count; i++)
            monitors.Add(Describe(areas[i]));
        var current = Describe(DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest));
        var target = WindowPlacementLogic.Resolve(saved, monitors, current);
        var b = target.Bounds;
        window.AppWindow.MoveAndResize(new RectInt32(b.Left, b.Top, b.Width, b.Height));
        if (target.Maximized && window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private static MonitorArea Describe(DisplayArea area)
    {
        var w = area.WorkArea;
        var dpi = 96;
        try
        {
            var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var x, out _) == 0 && x > 0)
                dpi = (int)x;
        }
        catch (Exception e) when (e is COMException or DllNotFoundException)
        {
            // keep 96
        }
        return new MonitorArea(new PixelRect(w.X, w.Y, w.Width, w.Height), dpi);
    }
}
