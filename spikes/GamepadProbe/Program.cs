// T12.1 spike S1: controller input in Native AOT. Throwaway; results go to D-056 and ARCHITECTURE → Footprint.
// Usage: GamepadProbe <gi|xi> [--seconds N] [--keys] [--quiet] [--threshold F] [--focus N]
//   gi    GameInput (inbox GameInput.dll, v0 API): device + reading callbacks, no polling.
//   xi    XInput 1.4 polled at 20 Hz on connected slots, only while a pad is present (arrival notifications).
//   --keys  also log keyboard/mouse input (low-level hooks) to see what Steam Input sends (gate g).
//   --quiet only the summary (for CPU windows). Arrival, suspend/resume are always logged.
// Ends after N seconds (default: until Ctrl+C) and prints own CPU ms, threads and private MB.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GamepadProbe;

internal static unsafe partial class Program
{
    static bool quiet, keys;
    static float threshold = 0.1f;
    static uint focusPolicy;
    static uint mainThread;
    static long readings, buttonChanges, arrivals, removals, polls;
    static uint lastButtons = uint.MaxValue;
    static readonly Stopwatch clock = Stopwatch.StartNew();

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("GamepadProbe <gi|xi> [--seconds N] [--keys] [--quiet] [--threshold F] [--focus N]"); return 2; }
        string mode = args[0];
        int seconds = 0;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[++i]); break;
                case "--keys": keys = true; break;
                case "--quiet": quiet = true; break;
                case "--threshold": threshold = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--focus": focusPolicy = uint.Parse(args[++i]); break;
            }
        }
        mainThread = GetCurrentThreadId();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; PostThreadMessageW(mainThread, WM_QUIT, 0, 0); };

        Power.Start();
        Arrival.Start();
        if (keys) Hooks.Start();
        bool ok = mode switch { "gi" => Gi.Start(), "xi" => Xi.Start(), _ => false };
        if (!ok) { Log($"mode {mode} failed to start"); return 1; }

        // Let start-up settle, then open the CPU window.
        Thread.Sleep(2000);
        var p = Process.GetCurrentProcess();
        Log($"started {mode}; CPU window opens (threads {p.Threads.Count}, private {p.PrivateMemorySize64 / 1048576.0:F1} MB)");
        p.Refresh();
        TimeSpan cpu0 = p.TotalProcessorTime;
        long t0 = clock.ElapsedMilliseconds;

        if (seconds > 0)
            new Thread(() => { Thread.Sleep(seconds * 1000); PostThreadMessageW(mainThread, WM_QUIT, 0, 0); }) { IsBackground = true }.Start();

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0) { _ = TranslateMessage(&msg); DispatchMessageW(&msg); }

        TimeSpan cpu1 = Process.GetCurrentProcess().TotalProcessorTime;
        p.Refresh();
        double cpuMs = (cpu1 - cpu0).TotalMilliseconds;
        double windowS = (clock.ElapsedMilliseconds - t0) / 1000.0;
        Console.WriteLine($"SUMMARY mode={mode} window={windowS:F0}s cpu={cpuMs:F0}ms threads={p.Threads.Count} " +
            $"private={p.PrivateMemorySize64 / 1048576.0:F1}MB ws={p.WorkingSet64 / 1048576.0:F1}MB readings={readings} " +
            $"buttonChanges={buttonChanges} polls={polls} arrivals={arrivals} removals={removals}");
        if (mode == "gi") Gi.Stop(); else Xi.Stop();
        Arrival.Stop();
        return 0;
    }

    internal static void Log(string s) => Console.WriteLine($"[{clock.Elapsed:mm\\:ss\\.fff}] {s}");

    internal static void OnButtons(string src, uint xboxButtons, float lt, float rt)
    {
        if (xboxButtons == lastButtons) return;
        lastButtons = xboxButtons;
        Interlocked.Increment(ref buttonChanges);
        if (quiet) return;
        Log($"{src} buttons [{Names(xboxButtons)}] LT {lt:F2} RT {rt:F2} | fg {ForegroundName()}");
    }

    // Canonical XInput-style bit names (D-057 order is the product's job; here just readable).
    static string Names(uint b)
    {
        if (b == 0) return "";
        var parts = new List<string>();
        ReadOnlySpan<(uint, string)> map = [(0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"), (0x100, "LB"), (0x200, "RB"),
            (0x20, "View"), (0x10, "Menu"), (0x40, "LS"), (0x80, "RS"), (0x1, "Up"), (0x2, "Down"), (0x4, "Left"), (0x8, "Right")];
        foreach (var (bit, n) in map) if ((b & bit) != 0) parts.Add(n);
        return string.Join('+', parts);
    }

    internal static string ForegroundName()
    {
        nint h = GetForegroundWindow();
        if (h == 0) return "(none)";
        GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return pid.ToString(); }
    }

    internal static bool Quiet => quiet;
    internal static float Threshold => threshold;
    internal static uint FocusPolicy => focusPolicy;
    internal static void CountReading() => Interlocked.Increment(ref readings);
    internal static void CountPoll() => Interlocked.Increment(ref polls);
    internal static void CountArrival(bool on) { if (on) Interlocked.Increment(ref arrivals); else Interlocked.Increment(ref removals); }

    const uint WM_QUIT = 0x12;
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }
    [LibraryImport("user32.dll")] private static partial int GetMessageW(MSG* m, nint h, uint a, uint b);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(MSG* m);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(MSG* m);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostThreadMessageW(uint t, uint m, nint w, nint l);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint h, out uint pid);
}

// ---------------- GameInput v0 (inbox GameInput.dll 0.2309) via raw vtables ----------------
internal static unsafe partial class Gi
{
    const uint KindGamepad = 0x00040000;
    const uint StatusConnected = 0x1;
    const uint AsyncEnumeration = 1;
    static void** gi; // IGameInput*
    static ulong devToken, readToken;

    [StructLayout(LayoutKind.Sequential)]
    struct GamepadState { public uint buttons; public float lt, rt, lx, ly, rx, ry; }

    public static bool Start()
    {
        void* p;
        int hr;
        try { hr = GameInputCreate(&p); }
        catch (Exception e) { Program.Log($"GameInputCreate threw {e.GetType().Name}: {e.Message}"); return false; }
        if (hr < 0) { Program.Log($"GameInputCreate hr=0x{hr:X8}"); return false; }
        gi = (void**)p;
        Program.Log("GameInputCreate ok");

        if (Program.FocusPolicy != 0)
        {
            ((delegate* unmanaged[Stdcall]<void*, uint, void>)Vt(21))(gi, Program.FocusPolicy);
            Program.Log($"SetFocusPolicy({Program.FocusPolicy})");
        }

        ulong t;
        hr = ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, void*, delegate* unmanaged[Stdcall]<ulong, void*, void*, ulong, uint, uint, void>, ulong*, int>)Vt(9))(
            gi, null, KindGamepad, StatusConnected, AsyncEnumeration, null, &OnDevice, &t);
        Program.Log($"RegisterDeviceCallback hr=0x{hr:X8}");
        if (hr < 0) return false;
        devToken = t;

        hr = ((delegate* unmanaged[Stdcall]<void*, void*, uint, float, void*, delegate* unmanaged[Stdcall]<ulong, void*, void*, byte, void>, ulong*, int>)Vt(8))(
            gi, null, KindGamepad, Program.Threshold, null, &OnReading, &t);
        Program.Log($"RegisterReadingCallback(threshold {Program.Threshold}) hr=0x{hr:X8}");
        if (hr < 0) return false;
        readToken = t;
        return true;
    }

    public static void Stop()
    {
        if (gi == null) return;
        var unreg = (delegate* unmanaged[Stdcall]<void*, ulong, ulong, byte>)Vt(13);
        byte a = unreg(gi, readToken, 5_000_000), b = unreg(gi, devToken, 5_000_000);
        uint refs = ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(2))(gi);
        Program.Log($"Unregister read={a} dev={b}; Release -> {refs}");
        gi = null;
    }

    static void* Vt(int i) => ((void**)*gi)[i];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void OnDevice(ulong token, void* ctx, void* device, ulong ts, uint now, uint before)
    {
        bool on = (now & StatusConnected) != 0, was = (before & StatusConnected) != 0;
        if (on != was) Program.CountArrival(on);
        Program.Log($"GI device {(nint)device:X} status 0x{before:X} -> 0x{now:X}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void OnReading(ulong token, void* ctx, void* reading, byte overrun)
    {
        Program.CountReading();
        void** r = (void**)reading;
        GamepadState s;
        byte ok = ((delegate* unmanaged[Stdcall]<void*, GamepadState*, byte>)((void**)*r)[22])(r, &s);
        if (ok == 0) return;
        Program.OnButtons("GI", ToXinputBits(s.buttons), s.lt, s.rt);
    }

    // GameInputGamepadButtons -> XINPUT_GAMEPAD_* bits so both readers log the same names.
    static uint ToXinputBits(uint g)
    {
        uint x = 0;
        if ((g & 0x1) != 0) x |= 0x10;      // Menu -> START
        if ((g & 0x2) != 0) x |= 0x20;      // View -> BACK
        if ((g & 0x4) != 0) x |= 0x1000;    // A
        if ((g & 0x8) != 0) x |= 0x2000;    // B
        if ((g & 0x10) != 0) x |= 0x4000;   // X
        if ((g & 0x20) != 0) x |= 0x8000;   // Y
        if ((g & 0x40) != 0) x |= 0x1;      // Up
        if ((g & 0x80) != 0) x |= 0x2;      // Down
        if ((g & 0x100) != 0) x |= 0x4;     // Left
        if ((g & 0x200) != 0) x |= 0x8;     // Right
        if ((g & 0x400) != 0) x |= 0x100;   // LB
        if ((g & 0x800) != 0) x |= 0x200;   // RB
        if ((g & 0x1000) != 0) x |= 0x40;   // LS
        if ((g & 0x2000) != 0) x |= 0x80;   // RS
        return x;
    }

    [LibraryImport("GameInput.dll")] private static partial int GameInputCreate(void** gameInput);
}

// ---------------- XInput 1.4, 20 Hz on connected slots, only while a pad is present ----------------
internal static unsafe partial class Xi
{
    [StructLayout(LayoutKind.Sequential)]
    struct State { public uint packet; public ushort buttons; public byte lt, rt; public short lx, ly, rx, ry; }

    static Thread? thread;
    static readonly AutoResetEvent wake = new(false);
    static volatile bool stop;

    public static bool Start()
    {
        Arrival.Changed += () => wake.Set();
        thread = new Thread(Run) { IsBackground = true, Name = "xinput" };
        thread.Start();
        return true;
    }

    public static void Stop() { stop = true; wake.Set(); thread?.Join(2000); }

    static void Run()
    {
        Span<bool> on = stackalloc bool[4];
        while (!stop)
        {
            // Which slots are connected? One scan, then poll only those; none → sleep until an arrival.
            int count = 0;
            State s;
            for (uint i = 0; i < 4; i++) { on[(int)i] = XInputGetState(i, &s) == 0; if (on[(int)i]) count++; }
            Program.Log($"XI scan: {count} pad(s) connected");
            if (count == 0) { wake.WaitOne(); continue; }

            while (!stop)
            {
                bool any = false;
                for (uint i = 0; i < 4; i++)
                {
                    if (!on[(int)i]) continue;
                    Program.CountPoll();
                    if (XInputGetState(i, &s) != 0) { on[(int)i] = false; continue; }
                    any = true;
                    Program.OnButtons($"XI{i}", s.buttons, s.lt / 255f, s.rt / 255f);
                }
                if (!any) break;           // last pad gone → rescan, then sleep
                if (wake.WaitOne(50)) break; // an arrival/resume → rescan
            }
        }
    }

    [LibraryImport("xinput1_4.dll")] private static partial uint XInputGetState(uint index, State* state);
}

// ---------------- Device-interface arrival (XUSB + HID) ----------------
internal static unsafe partial class Arrival
{
    public static event Action? Changed;
    static readonly Guid Xusb = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    static readonly Guid Hid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    static nint hXusb, hHid;

    public static void Start()
    {
        hXusb = Register(Xusb);
        hHid = Register(Hid);
        Program.Log($"CM_Register_Notification xusb={(hXusb != 0)} hid={(hHid != 0)}");
    }

    public static void Stop() { if (hXusb != 0) _ = CM_Unregister_Notification(hXusb); if (hHid != 0) _ = CM_Unregister_Notification(hHid); }

    static nint Register(Guid g)
    {
        byte* filter = stackalloc byte[416];
        new Span<byte>(filter, 416).Clear();
        *(uint*)filter = 416;          // cbSize
        *(int*)(filter + 8) = 0;       // CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE
        *(Guid*)(filter + 16) = g;     // u.DeviceInterface.ClassGuid
        nint h;
        uint cr = CM_Register_Notification(filter, null, &OnNotify, &h);
        if (cr != 0) Program.Log($"CM_Register_Notification cr={cr}");
        return cr == 0 ? h : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint OnNotify(nint h, void* ctx, int action, byte* data, uint size)
    {
        if (action is not (0 or 1)) return 0; // INTERFACEARRIVAL / INTERFACEREMOVAL
        Guid cls = *(Guid*)(data + 8);
        string link = new((char*)(data + 24));
        bool xusb = cls == Xusb;
        // HID: only game controllers interest us; log Microsoft (045E) and Valve (28DE) devices, skip the rest quietly.
        if (!xusb && !link.Contains("VID_045E", StringComparison.OrdinalIgnoreCase) && !link.Contains("VID_28DE", StringComparison.OrdinalIgnoreCase)
            && !link.Contains("IG_", StringComparison.OrdinalIgnoreCase)) return 0;
        Program.Log($"arrival {(action == 0 ? "+" : "-")} {(xusb ? "XUSB" : "HID")} {link}");
        if (xusb || link.Contains("IG_", StringComparison.OrdinalIgnoreCase)) Program.CountArrival(action == 0);
        Changed?.Invoke();
        return 0;
    }

    [LibraryImport("cfgmgr32.dll")] private static partial uint CM_Register_Notification(byte* filter, void* ctx, delegate* unmanaged[Stdcall]<nint, void*, int, byte*, uint, uint> cb, nint* h);
    [LibraryImport("cfgmgr32.dll")] private static partial uint CM_Unregister_Notification(nint h);
}

// ---------------- Suspend/resume ----------------
internal static unsafe partial class Power
{
    static nint handle;
    public static void Start()
    {
        var p = stackalloc nint[2];
        p[0] = (nint)(delegate* unmanaged[Stdcall]<void*, uint, void*, uint>)&OnPower;
        p[1] = 0;
        uint r = PowerRegisterSuspendResumeNotification(2 /* DEVICE_NOTIFY_CALLBACK */, p, out handle);
        Program.Log($"PowerRegisterSuspendResumeNotification r={r}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint OnPower(void* ctx, uint type, void* setting)
    {
        string what = type switch { 4 => "suspend", 7 => "resume (user)", 0x12 => "resume (auto)", _ => $"0x{type:X}" };
        Program.Log($"power {what}");
        return 0;
    }

    [LibraryImport("powrprof.dll")] private static partial uint PowerRegisterSuspendResumeNotification(uint flags, void* recipient, out nint handle);
}

// ---------------- Low-level hooks: what does Steam Input send? (gate g) ----------------
internal static unsafe partial class Hooks
{
    static nint kb, ms;
    public static void Start()
    {
        nint mod = GetModuleHandleW(null);
        kb = SetWindowsHookExW(13, &OnKey, mod, 0);
        ms = SetWindowsHookExW(14, &OnMouse, mod, 0);
        Program.Log($"hooks kb={(kb != 0)} mouse={(ms != 0)}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint OnKey(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            uint vk = *(uint*)l, flags = *(uint*)(l + 8);
            string dir = (w == 0x100 || w == 0x104) ? "down" : "up";
            Program.Log($"KEY vk=0x{vk:X2} {dir}{((flags & 0x10) != 0 ? " injected" : "")}");
        }
        return CallNextHookEx(0, code, w, l);
    }

    static long lastMoveLog;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint OnMouse(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            uint flags = *(uint*)(l + 12);
            if (w == 0x200) // WM_MOUSEMOVE: at most one line a second
            {
                long now = Environment.TickCount64;
                if (now - lastMoveLog > 1000) { lastMoveLog = now; Program.Log($"MOUSE move{((flags & 1) != 0 ? " injected" : "")}"); }
            }
            else Program.Log($"MOUSE msg=0x{w:X}{((flags & 1) != 0 ? " injected" : "")}");
        }
        return CallNextHookEx(0, code, w, l);
    }

    [LibraryImport("user32.dll")] private static partial nint SetWindowsHookExW(int id, delegate* unmanaged[Stdcall]<int, nint, nint, nint> fn, nint mod, uint thread);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint h, int c, nint w, nint l);
}
