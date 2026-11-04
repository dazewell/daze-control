// Throwaway spike S2 (T12.2). Plain DllImport: this probe is not AOT and not in the solution.
using System.Runtime.InteropServices;

namespace MenuProbe;

internal delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);
internal delegate nint HookProc(int code, nint w, nint l);

[StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; public readonly int W => Right - Left; public readonly int H => Bottom - Top; public override readonly string ToString() => $"({Left},{Top} {W}x{H})"; }
[StructLayout(LayoutKind.Sequential)] internal struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEX { public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm; }
[StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
[StructLayout(LayoutKind.Sequential)] internal struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint extra; }
[StructLayout(LayoutKind.Sequential)] internal struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rc; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }

[StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint extra; }
[StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint extra; }
[StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public INPUTUNION u; }

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

internal static class N
{
    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    public const uint WM_DESTROY = 2, WM_SIZE = 5, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_KEYDOWN = 0x100, WM_HOTKEY = 0x312,
        WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEWHEEL = 0x20A, WM_MOUSEACTIVATE = 0x21,
        WM_TIMER = 0x113, WM_DPICHANGED = 0x2E0, WM_APP = 0x8000, WM_ERASEBKGND = 0x14, WM_NCHITTEST = 0x84;
    public const int MA_NOACTIVATE = 3, SW_SHOW = 5, SW_SHOWNOACTIVATE = 4;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const int WH_MOUSE_LL = 14;

    [DllImport("user32", SetLastError = true)] public static extern nint SetProcessDpiAwarenessContext(nint ctx);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] public static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32")] public static extern int GetMessageW(out MSG m, nint h, uint min, uint max);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG m);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] public static extern bool PostMessageW(nint h, uint m, nint w, nint l);
    [DllImport("user32")] public static extern bool DestroyWindow(nint h);
    [DllImport("user32")] public static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32")] public static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32")] public static extern nint GetForegroundWindow();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint h, char[] buf, int n);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32")] public static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32")] public static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32")] public static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32")] public static extern nint MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32")] public static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint h);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(nint mon, int type, out uint dx, out uint dy);
    [DllImport("user32", SetLastError = true)] public static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32", SetLastError = true)] public static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32", SetLastError = true)] public static extern nint SetWindowsHookExW(int id, HookProc proc, nint mod, uint thread);
    [DllImport("user32")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] public static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [DllImport("user32")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] public static extern bool InvalidateRect(nint h, nint r, bool erase);
    [DllImport("user32")] public static extern nint BeginPaint(nint h, out PAINTSTRUCT ps);
    [DllImport("user32")] public static extern bool EndPaint(nint h, ref PAINTSTRUCT ps);
    [DllImport("user32")] public static extern nint SetTimer(nint h, nint id, uint ms, nint proc);
    [DllImport("user32")] public static extern bool KillTimer(nint h, nint id);
    [DllImport("user32")] public static extern nint GetDC(nint h);
    [DllImport("user32")] public static extern int ReleaseDC(nint h, nint dc);
    [DllImport("user32")] public static extern int FillRect(nint dc, ref RECT r, nint brush);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int DrawTextW(nint dc, string s, int n, ref RECT r, uint fmt);
    [DllImport("user32")] public static extern nint LoadCursorW(nint inst, nint id);
    [DllImport("user32")] public static extern bool IsWindow(nint h);
    [DllImport("user32")] public static extern int ShowCursor(bool show);

    [DllImport("gdi32")] public static extern nint CreateSolidBrush(uint rgb);
    [DllImport("gdi32")] public static extern bool DeleteObject(nint o);
    [DllImport("gdi32")] public static extern nint SelectObject(nint dc, nint o);
    [DllImport("gdi32")] public static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32")] public static extern uint SetTextColor(nint dc, uint c);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] public static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] public static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32")] public static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32")] public static extern bool DeleteDC(nint dc);

    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint h, int attr, ref int value, int size);
    [DllImport("shell32")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32")] public static extern nint GetModuleHandleW(nint name);

    public static string Title(nint h) { var b = new char[256]; int n = GetWindowTextW(h, b, b.Length); return new string(b, 0, n); }
    public static string Describe(nint h) { GetWindowThreadProcessId(h, out uint pid); return $"0x{h:X} pid {pid} \"{Title(h)}\""; }

    public static void Key(ushort vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wVk = vk; i.u.ki.dwFlags = up ? 2u : 0u; SendInput(1, [i], Marshal.SizeOf<INPUT>()); }
    public static void Tap(ushort vk) { Key(vk, false); Thread.Sleep(30); Key(vk, true); }
    public static void Click() { var d = new INPUT { type = 0 }; d.u.mi.dwFlags = 2; var u = new INPUT { type = 0 }; u.u.mi.dwFlags = 4; SendInput(1, [d], Marshal.SizeOf<INPUT>()); Thread.Sleep(40); SendInput(1, [u], Marshal.SizeOf<INPUT>()); }
    public static void MoveBy(int dx, int dy) { var m = new INPUT { type = 0 }; m.u.mi.dx = dx; m.u.mi.dy = dy; m.u.mi.dwFlags = 1; SendInput(1, [m], Marshal.SizeOf<INPUT>()); }
    [DllImport("user32")] public static extern void SwitchToThisWindow(nint h, bool altTab);
    public static void Wheel(int delta) { var w = new INPUT { type = 0 }; w.u.mi.dwFlags = 0x800; w.u.mi.mouseData = unchecked((uint)delta); SendInput(1, [w], Marshal.SizeOf<INPUT>()); }

    public static RECT MonitorRect(nint mon, bool work) { var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() }; GetMonitorInfoW(mon, ref mi); return work ? mi.rcWork : mi.rcMonitor; }

    /// <summary>Screen capture of one rect to a 32-bit BMP (DWM-composed content only).</summary>
    public static void Capture(RECT r, string path)
    {
        nint screen = GetDC(0), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = r.W, biHeight = -r.H, biPlanes = 1, biBitCount = 32 };
        nint bmp = CreateDIBSection(screen, ref bi, 0, out nint bits, 0, 0);
        nint old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, r.W, r.H, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        int size = r.W * r.H * 4;
        var data = new byte[size]; Marshal.Copy(bits, data, 0, size);
        using (var f = File.Create(path))
        using (var w = new BinaryWriter(f))
        {
            w.Write((ushort)0x4D42); w.Write(54 + size); w.Write(0); w.Write(54);
            w.Write(40); w.Write(r.W); w.Write(-r.H); w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(data);
        }
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(0, screen);
    }
}
