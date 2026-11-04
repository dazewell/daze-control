// Throwaway D3D11 "game" for spike S2: clears to a cycling colour every vsync and logs frames per 250 ms.
// Modes: borderless (WS_POPUP over the monitor, flip model), fso (flip model + SetFullscreenState(TRUE): fullscreen
// optimisations turn it into independent flip), exclusive (blt model + SetFullscreenState(TRUE); the runner turns
// FSO off for this exe through the per-user AppCompat layer DISABLEDXMAXIMIZEDWINDOWEDMODE).
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MenuProbe;

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SWAP_CHAIN_DESC
{
    public uint Width, Height, RefreshNum, RefreshDen, Format, Scanline, Scaling;
    public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
    public nint OutputWindow;
    public int Windowed;
    public uint SwapEffect, Flags;
}

internal static unsafe class Game
{
    [DllImport("d3d11")]
    private static extern int D3D11CreateDeviceAndSwapChain(nint adapter, int driverType, nint software, uint flags, nint levels, uint nLevels, uint sdk,
        ref DXGI_SWAP_CHAIN_DESC desc, out nint swap, out nint device, out int level, out nint context);

    private static nint s_swap, s_device, s_context, s_rtv;
    private static WndProc? s_proc;
    private static volatile bool s_resize;

    private static nint V(nint obj, int slot) => (*(nint**)obj)[slot];
    private static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)V(obj, 2))(obj); }

    private static void MakeTarget()
    {
        Guid tex = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
        nint buf;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)V(s_swap, 9))(s_swap, 0, &tex, &buf);
        Check(hr, "GetBuffer");
        nint rtv;
        hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)V(s_device, 9))(s_device, buf, 0, &rtv);
        Check(hr, "CreateRenderTargetView");
        Release(buf);
        s_rtv = rtv;
    }

    private static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} 0x{hr:X8}"); }

    public static int Run(string mode, int seconds, string log)
    {
        using var w = new StreamWriter(log) { AutoFlush = true };
        w.WriteLine($"game {mode} pid {Environment.ProcessId}");
        nint inst = N.GetModuleHandleW(0);
        s_proc = (h, m, wp, lp) =>
        {
            if (m == N.WM_SIZE) { s_resize = true; w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} WM_SIZE type {wp}"); }
            if (m == 0x1C) w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} WM_ACTIVATEAPP {wp}");
            if (m == 0x6) w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} WM_ACTIVATE {wp & 0xFFFF} other 0x{lp:X}");
            if (m == N.WM_DESTROY) { N.PostQuitMessage(0); return 0; }
            return N.DefWindowProcW(h, m, wp, lp);
        };
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc), hInstance = inst, lpszClassName = "S2.Game", hCursor = N.LoadCursorW(0, 32512) };
        N.RegisterClassExW(ref wc);
        var mon = N.MonitorRect(N.MonitorFromPoint(new POINT(), 1), false);
        bool borderless = mode == "borderless";
        nint hwnd = borderless
            ? N.CreateWindowExW(0, "S2.Game", "S2 test game (borderless)", N.WS_POPUP | N.WS_VISIBLE, mon.Left, mon.Top, mon.W, mon.H, 0, 0, inst, 0)
            : N.CreateWindowExW(0, "S2.Game", $"S2 test game ({mode})", N.WS_POPUP | N.WS_VISIBLE, mon.Left, mon.Top, mon.W, mon.H, 0, 0, inst, 0);
        bool fg = N.SetForegroundWindow(hwnd);
        if (!fg) { N.SwitchToThisWindow(hwnd, true); Thread.Sleep(200); fg = N.GetForegroundWindow() == hwnd; w.WriteLine($"SwitchToThisWindow -> foreground {fg}"); }
        if (!fg)
        {   // Foreground lock (nothing in front, or another app): the Alt trick, test app only.
            N.Key(0x12, false); fg = N.SetForegroundWindow(hwnd); N.Key(0x12, true);
            Thread.Sleep(200); w.WriteLine($"Alt + SetForegroundWindow -> {fg}, foreground is me {N.GetForegroundWindow() == hwnd}");
        }
        w.WriteLine($"hwnd 0x{hwnd:X} SetForegroundWindow={fg} foreground={N.Describe(N.GetForegroundWindow())}");

        var desc = new DXGI_SWAP_CHAIN_DESC
        {
            Width = (uint)mon.W, Height = (uint)mon.H, // native: full screen must not change the display mode
            Format = 87, // B8G8R8A8_UNORM
            SampleCount = 1, BufferUsage = 0x20,
            BufferCount = mode == "exclusive" ? 1u : 2u,
            OutputWindow = hwnd, Windowed = 1,
            SwapEffect = mode == "exclusive" ? 0u : 4u, // DISCARD (blt) : FLIP_DISCARD
            Flags = 2, // ALLOW_MODE_SWITCH
        };
        int hr = D3D11CreateDeviceAndSwapChain(0, 1, 0, 0, 0, 0, 7, ref desc, out s_swap, out s_device, out _, out s_context);
        Check(hr, "D3D11CreateDeviceAndSwapChain");
        // DXGI's own Alt+Enter would fight the runner; the runner drives full screen explicitly.
        MakeTarget();
        if (!borderless)
        {
            for (int i = 0; i < 10; i++)
            {
                hr = ((delegate* unmanaged[Stdcall]<nint, int, nint, int>)V(s_swap, 10))(s_swap, 1, 0);
                w.WriteLine($"SetFullscreenState(TRUE) 0x{hr:X8}");
                if (hr == 0) break;
                Thread.Sleep(200);
            }
        }

        var sw = Stopwatch.StartNew();
        long frames = 0, lastFrames = 0; double lastLog = 0;
        float* color = stackalloc float[4];
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            while (PeekMessageW(out var msg, 0, 0, 0, 1))
            {
                if (msg.message == 0x12) goto done; // WM_QUIT
                N.TranslateMessage(ref msg); N.DispatchMessageW(ref msg);
            }
            if (s_resize)
            {
                s_resize = false;
                Release(s_rtv); s_rtv = 0;
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, int>)V(s_swap, 13))(s_swap, 0, 0, 0, 0, 2);
                if (hr >= 0) MakeTarget();
                w.WriteLine($"{sw.Elapsed.TotalSeconds:F2} ResizeBuffers 0x{hr:X8}");
            }
            double t = sw.Elapsed.TotalSeconds;
            color[0] = (float)(0.5 + 0.5 * Math.Sin(t * 2)); color[1] = (float)(0.5 + 0.5 * Math.Sin(t * 2 + 2)); color[2] = (float)(0.5 + 0.5 * Math.Sin(t * 2 + 4)); color[3] = 1;
            if (s_rtv != 0)
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)V(s_context, 50))(s_context, s_rtv, color);
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)V(s_swap, 8))(s_swap, 1, 0);
            frames++;
            if (t - lastLog >= 0.25)
            {
                int fs = 0; nint target;
                ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)V(s_swap, 11))(s_swap, &fs, &target);
                if (target != 0) Release(target);
                w.WriteLine($"{t:F2} fps {(frames - lastFrames) / (t - lastLog):F0} present 0x{hr:X8} fullscreen {fs} fgIsMe {N.GetForegroundWindow() == hwnd}");
                lastFrames = frames; lastLog = t;
            }
        }
    done:
        if (!borderless) ((delegate* unmanaged[Stdcall]<nint, int, nint, int>)V(s_swap, 10))(s_swap, 0, 0);
        Release(s_rtv); Release(s_swap); Release(s_context); Release(s_device);
        N.DestroyWindow(hwnd);
        w.WriteLine($"done frames {frames}");
        return 0;
    }

    [DllImport("user32")] private static extern bool PeekMessageW(out MSG m, nint h, uint min, uint max, uint remove);
}
