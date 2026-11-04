// Spike S2 menu: WS_POPUP + NOACTIVATE|TOOLWINDOW|TOPMOST, MA_NOACTIVATE, keys borrowed with RegisterHotKey while
// open, a pass-through WH_MOUSE_LL hook for "click outside". GDI drawing (the gate is window behaviour, not acrylic).
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MenuProbe;

internal static class Menu
{
    public static readonly (string Name, uint Mods, uint Vk)[] Keys =
    [
        ("Up", 0, 0x26), ("Down", 0, 0x28), ("Left", 0, 0x25), ("Right", 0, 0x27), ("Enter", 0, 0x0D), ("Space", 0, 0x20),
        ("Esc", 0, 0x1B), ("Tab", 0, 0x09), ("Shift+Tab", 4, 0x09), ("Home", 0, 0x24), ("End", 0, 0x23),
        ("1", 0, 0x31), ("2", 0, 0x32), ("3", 0, 0x33), ("4", 0, 0x34), ("5", 0, 0x35), ("6", 0, 0x36), ("7", 0, 0x37), ("8", 0, 0x38), ("9", 0, 0x39),
    ];
    private static readonly string[] Items = ["Connect TV", "Couch audio", "Game mode", "Back to desk", "Steam Big Picture"];
    private const int WidthDip = 400, PadDip = 16, TitleDip = 32, RowDip = 56, GapDip = 4;
    private const uint Cancel = N.WM_APP + 1, Shot = N.WM_APP + 2;

    private static StreamWriter s_log = null!;
    private static nint s_hwnd, s_hook;
    private static int s_focus, s_hover = -1;
    private static uint s_dpi = 96;
    private static readonly bool[] s_registered = new bool[Keys.Length];
    private static WndProc? s_proc;
    private static HookProc? s_hookProc;
    private static readonly List<double> s_hookUs = [], s_chainUs = [];
    private static string s_result = "none";
    private static volatile bool s_closed;
    private static int Px(int dip) => (int)Math.Round(dip * s_dpi / 96.0);
    private static void Log(string s) => s_log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");

    public static int Run(string script, string log, string? shot, bool force, int closeAfterMs)
    {
        using var w = new StreamWriter(log) { AutoFlush = true };
        s_log = w;
        nint fgBefore = N.GetForegroundWindow();
        Log($"foreground before: {N.Describe(fgBefore)}");
        N.SHQueryUserNotificationState(out int quns);
        Log($"SHQueryUserNotificationState = {quns} ({QunsName(quns)})");
        if (quns == 3 && !force) { Log("refused: QUNS_RUNNING_D3D_FULL_SCREEN -> step would fail with #32; nothing shown, no key taken"); return 32; }

        nint inst = N.GetModuleHandleW(0);
        s_proc = WndProc; s_hookProc = Hook;
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc), hInstance = inst, lpszClassName = "S2.Menu", hCursor = N.LoadCursorW(0, 32512) };
        N.RegisterClassExW(ref wc);

        // Placement: the foreground window's monitor; monitor rect if it covers it, else the work area.
        nint mon = N.MonitorFromWindow(fgBefore, 2);
        N.GetDpiForMonitor(mon, 0, out s_dpi, out _);
        RECT mr = N.MonitorRect(mon, false), wr = N.MonitorRect(mon, true);
        N.GetWindowRect(fgBefore, out var fr);
        bool covers = fr.Left <= mr.Left && fr.Top <= mr.Top && fr.Right >= mr.Right && fr.Bottom >= mr.Bottom;
        RECT area = covers ? mr : wr;
        int width = Px(WidthDip), height = Px(PadDip * 2 + TitleDip + GapDip + Items.Length * RowDip + (Items.Length - 1) * GapDip);
        int x = area.Left + (area.W - width) / 2, y = area.Top + (area.H - height) / 2;
        Log($"monitor {mr} work {wr} dpi {s_dpi} ({s_dpi * 100 / 96} %) fg covers monitor {covers}; menu {width}x{height} px = {WidthDip} DIP wide");

        string variant = Environment.GetEnvironmentVariable("S2_VARIANT") ?? "";
        uint ex = N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW | N.WS_EX_TOPMOST;
        if (variant == "layered") ex |= 0x80000; // WS_EX_LAYERED
        if (variant == "notopmost") ex &= ~N.WS_EX_TOPMOST;
        if (variant == "small") { width = Px(120); height = Px(40); x = area.Left + Px(24); y = area.Top + Px(24); }
        Log($"variant '{variant}'");
        s_hwnd = N.CreateWindowExW(ex, "S2.Menu", "Couch menu", N.WS_POPUP, x, y, width, height, 0, 0, inst, 0);
        if (variant == "layered") SetLayeredWindowAttributes(s_hwnd, 0, 255, 2);
        int round = 2; N.DwmSetWindowAttribute(s_hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = round
        if (variant != "nowindow") N.ShowWindow(s_hwnd, N.SW_SHOWNOACTIVATE);
        if (variant is not "nowindow" and not "notopmost") N.SetWindowPos(s_hwnd, N.HWND_TOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE | N.SWP_SHOWWINDOW);
        N.GetWindowRect(s_hwnd, out var actual);
        Log($"shown: rect {actual} GetDpiForWindow {N.GetDpiForWindow(s_hwnd)}; foreground now {N.Describe(N.GetForegroundWindow())}");

        for (int i = 0; i < Keys.Length; i++)
        {
            s_registered[i] = N.RegisterHotKey(s_hwnd, 100 + i, Keys[i].Mods, Keys[i].Vk);
            if (!s_registered[i]) Log($"RegisterHotKey {Keys[i].Name} failed {Marshal.GetLastWin32Error()}");
        }
        Log($"registered {s_registered.Count(r => r)}/{Keys.Length} keys");
        s_hook = N.SetWindowsHookExW(N.WH_MOUSE_LL, s_hookProc, inst, 0);
        Log($"mouse hook {(s_hook != 0 ? "on" : "FAILED " + Marshal.GetLastWin32Error())}");
        if (closeAfterMs > 0) N.SetTimer(s_hwnd, 1, (uint)closeAfterMs, 0);

        // Foreground watcher (spike only) + scripted input.
        var fgChanges = new List<string>();
        var watcher = new Thread(() => { nint last = fgBefore; while (!s_closed) { nint f = N.GetForegroundWindow(); if (f != last) { fgChanges.Add($"{DateTime.Now:HH:mm:ss.fff} -> {N.Describe(f)}"); last = f; } Thread.Sleep(20); } }) { IsBackground = true };
        watcher.Start();
        N.GetCursorPos(out var cursor0);
        var driver = new Thread(() => Drive(script, shot, mr, actual)) { IsBackground = true };
        driver.Start();

        while (N.GetMessageW(out var msg, 0, 0, 0) > 0) { N.TranslateMessage(ref msg); N.DispatchMessageW(ref msg); }
        s_closed = true;
        driver.Join(3000);
        N.SetCursorPos(cursor0.X, cursor0.Y);

        nint fgAfter = N.GetForegroundWindow();
        Log($"result: {s_result}");
        Log($"foreground after: {N.Describe(fgAfter)}; same as before: {fgAfter == fgBefore}");
        Log($"foreground changes while open: {(fgChanges.Count == 0 ? "none" : string.Join("; ", fgChanges))}");
        static string Stats(List<double> l) { var a = l.Skip(3).Order().ToArray(); return a.Length == 0 ? "n/a" : $"n {a.Length} avg {a.Average():F1} us, p99 {a[(int)(a.Length * 0.99)]:F1} us, max {a[^1]:F1} us (first 3 dropped as JIT; first was {l[0]:F0} us)"; }
        if (s_hookUs.Count > 0)
        {
            Log($"hook own time: {Stats(s_hookUs)}");
            Log($"CallNextHookEx (other apps' hooks after ours): {Stats(s_chainUs)}");
        }
        // In-process re-registration check (the runner also checks from a second process).
        bool again = N.RegisterHotKey(0, 999, 0, 0x26);
        Log($"after close, RegisterHotKey(Up) from a fresh registration in this process: {again}");
        if (again) N.UnregisterHotKey(0, 999);
        return 0;
    }

    [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);

    private static string QunsName(int q) => q switch { 1 => "NOT_PRESENT", 2 => "BUSY", 3 => "RUNNING_D3D_FULL_SCREEN", 4 => "PRESENTATION_MODE", 5 => "ACCEPTS_NOTIFICATIONS", 6 => "QUIET_TIME", 7 => "APP", _ => "?" };

    private static void Drive(string script, string? shot, RECT monitor, RECT menu)
    {
        Thread.Sleep(500);
        int rowY(int i) => menu.Top + Px(PadDip + TitleDip + GapDip + i * (RowDip + GapDip) + RowDip / 2);
        int cx = menu.Left + menu.W / 2;
        void Snap() { if (shot is not null) { N.Capture(monitor, shot); Log($"screenshot {shot}"); } }
        switch (script)
        {
            case "keys":
                foreach (ushort vk in new ushort[] { 0x28, 0x28, 0x28, 0x26, 0x23, 0x24, 0x09, 0x09 }) { N.Tap(vk); Thread.Sleep(150); }
                N.Key(0x10, false); N.Tap(0x09); N.Key(0x10, true); Thread.Sleep(200); // Shift+Tab
                Snap(); Thread.Sleep(200);
                N.Tap(0x0D); break;
            case "digit": Snap(); N.Tap(0x34); break;
            case "space": N.Tap(0x28); Thread.Sleep(150); N.Tap(0x20); break;
            case "mouse":
                N.SetCursorPos(cx, rowY(1)); Thread.Sleep(150);
                for (int i = 0; i < 40; i++) { N.MoveBy(0, (rowY(3) - rowY(1)) / 40); Thread.Sleep(10); }
                N.SetCursorPos(cx, rowY(3)); Thread.Sleep(50);
                N.Wheel(-120); Thread.Sleep(300);
                Snap(); Thread.Sleep(200);
                N.Click(); break;
            case "outside":
                Snap();
                N.SetCursorPos(menu.Left - 200, menu.Top + 50); Thread.Sleep(200); N.Click(); break;
            case "esc": Snap(); N.Tap(0x1B); break;
            case "stepcancel": Snap(); Thread.Sleep(1000); N.PostMessageW(s_hwnd, Cancel, 0, 0); break;
            case "timeout": Snap(); break; // the close timer ends it
            case "hold": Snap(); break;      // the runner kills the process
        }
    }

    private static void Close(string why)
    {
        if (s_closed) return;
        s_closed = true;
        s_result = why;
        int freed = 0;
        for (int i = 0; i < Keys.Length; i++) if (s_registered[i] && N.UnregisterHotKey(s_hwnd, 100 + i)) freed++;
        if (s_hook != 0) N.UnhookWindowsHookEx(s_hook);
        N.KillTimer(s_hwnd, 1);
        Log($"close ({why}): unregistered {freed}/{s_registered.Count(r => r)} keys, hook off");
        N.DestroyWindow(s_hwnd);
    }

    private static void Move(int to) { s_focus = Math.Clamp(to, 0, Items.Length - 1); N.InvalidateRect(s_hwnd, 0, false); }

    private static nint Hook(int code, nint w, nint l)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (code >= 0 && (uint)w == N.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
            N.GetWindowRect(s_hwnd, out var r);
            if (info.pt.X < r.Left || info.pt.X >= r.Right || info.pt.Y < r.Top || info.pt.Y >= r.Bottom)
                N.PostMessageW(s_hwnd, Cancel, 1, 0); // post, never close inside the hook
        }
        long t1 = Stopwatch.GetTimestamp();
        nint res = N.CallNextHookEx(s_hook, code, w, l);
        s_hookUs.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMicroseconds);
        s_chainUs.Add(Stopwatch.GetElapsedTime(t1).TotalMicroseconds);
        return res;
    }

    private static int RowAt(int yClient)
    {
        int y = yClient - Px(PadDip + TitleDip + GapDip);
        if (y < 0) return -1;
        int i = y / Px(RowDip + GapDip);
        return i < Items.Length ? i : -1;
    }

    private static nint WndProc(nint h, uint m, nint w, nint l)
    {
        switch (m)
        {
            case N.WM_MOUSEACTIVATE: return N.MA_NOACTIVATE;
            case N.WM_HOTKEY:
            {
                int id = (int)w - 100;
                string k = id >= 0 && id < Keys.Length ? Keys[id].Name : "?";
                Log($"hotkey {k}");
                switch (k)
                {
                    case "Up" or "Left" or "Shift+Tab": Move(s_focus - 1); break;
                    case "Down" or "Right" or "Tab": Move(s_focus + 1); break;
                    case "Home": Move(0); break;
                    case "End": Move(Items.Length - 1); break;
                    case "Enter" or "Space": Close($"picked {Items[s_focus]} with {k}"); break;
                    case "Esc": Close("cancelled with Esc"); break;
                    default: if (k.Length == 1 && k[0] - '1' < Items.Length) Close($"picked {Items[k[0] - '1']} with digit {k}"); break;
                }
                return 0;
            }
            case N.WM_MOUSEMOVE:
            {
                int row = RowAt((short)((long)l >> 16));
                if (row >= 0 && row != s_hover) { s_hover = row; Move(row); Log($"hover {Items[row]}"); }
                return 0;
            }
            case N.WM_MOUSEWHEEL: Log($"wheel {(short)((long)w >> 16)}"); return 0;
            case N.WM_LBUTTONUP:
            {
                int row = RowAt((short)((long)l >> 16));
                if (row >= 0) Close($"picked {Items[row]} with a click");
                return 0;
            }
            case Cancel: Close(w == 1 ? "click outside" : "step cancelled"); return 0;
            case N.WM_TIMER: Close("close timer"); return 0;
            case N.WM_DPICHANGED: s_dpi = (uint)((long)w & 0xFFFF); Log($"WM_DPICHANGED {s_dpi}"); N.InvalidateRect(h, 0, true); return 0;
            case N.WM_ERASEBKGND: return 1;
            case N.WM_PAINT: Paint(h); return 0;
            case N.WM_DESTROY: N.PostQuitMessage(0); return 0;
        }
        return N.DefWindowProcW(h, m, w, l);
    }

    private static void Paint(nint h)
    {
        nint dc = N.BeginPaint(h, out var ps);
        N.GetClientRect(h, out var rc);
        nint bg = N.CreateSolidBrush(0x202020), accent = N.CreateSolidBrush(0xD47800), row = N.CreateSolidBrush(0x2D2D2D);
        N.FillRect(dc, ref rc, bg);
        nint title = N.CreateFontW(-Px(20), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint body = N.CreateFontW(-Px(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint bold = N.CreateFontW(-Px(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        N.SetBkMode(dc, 1);
        N.SetTextColor(dc, 0xFFFFFF);
        nint old = N.SelectObject(dc, title);
        var tr = new RECT { Left = Px(PadDip), Top = Px(PadDip), Right = rc.Right - Px(PadDip), Bottom = Px(PadDip + TitleDip) };
        N.DrawTextW(dc, "Couch menu", -1, ref tr, 0x24); // VCENTER|SINGLELINE
        N.SelectObject(dc, body); N.SetTextColor(dc, 0xC8C8C8);
        N.DrawTextW(dc, $"{Items.Length} choices", -1, ref tr, 0x26); // RIGHT
        for (int i = 0; i < Items.Length; i++)
        {
            int top = Px(PadDip + TitleDip + GapDip + i * (RowDip + GapDip));
            var r = new RECT { Left = Px(PadDip), Top = top, Right = rc.Right - Px(PadDip), Bottom = top + Px(RowDip) };
            N.FillRect(dc, ref r, i == s_focus ? accent : row);
            var t = r; t.Left += Px(16);
            N.SelectObject(dc, i == s_focus ? bold : body);
            N.SetTextColor(dc, i == s_focus ? 0x000000u : 0xFFFFFFu);
            N.DrawTextW(dc, $"{i + 1}   {Items[i]}", -1, ref t, 0x24);
        }
        N.SelectObject(dc, old);
        foreach (var o in new[] { bg, accent, row, title, body, bold }) N.DeleteObject(o);
        N.EndPaint(h, ref ps);
    }
}
