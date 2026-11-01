using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Engine.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

/// <summary>
/// The engine's one hidden window on its STA message thread; tray icon and hotkeys hang off it (ARCHITECTURE
/// → Threading). Top-level rather than message-only so the TaskbarCreated broadcast reaches it (OSD D-009).
/// Never shown. One per process: the window procedure is a static <c>UnmanagedCallersOnly</c> function (AOT).
/// </summary>
internal sealed unsafe class MessageWindow : IDisposable
{
    /// <summary>Scripts find the engine by this class (scripts/stop.ps1, build skill).</summary>
    public const string ClassName = "DazeControl.Tray";

    private const uint InvokeMessage = PInvoke.WM_APP + 100;

    private static MessageWindow? s_current;
    private readonly List<Handler> _handlers = [];
    private readonly ConcurrentQueue<Action> _posted = new();

    /// <summary>Returns true when it handled the message.</summary>
    public delegate bool Handler(uint message, WPARAM wParam, LPARAM lParam);

    public MessageWindow()
    {
        if (s_current is not null)
            throw new InvalidOperationException("Only one MessageWindow per process.");
        s_current = this;
        var instance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(&windowClass) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            Handle = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, null, 0, 0, 0, 0, 0,
                HWND.Null, HMENU.Null, instance, null);
        }
        if (Handle == HWND.Null)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
    }

    public HWND Handle { get; }

    public void Add(Handler handler) => _handlers.Add(handler);

    /// <summary>Runs until the window is destroyed (WM_CLOSE from the tray's Exit or scripts/stop.ps1).</summary>
    public static void RunMessageLoop()
    {
        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0) > 0)
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the message thread, after the current message; safe from any thread.</summary>
    public void Post(Action action)
    {
        _posted.Enqueue(action);
        PInvoke.PostMessage(Handle, InvokeMessage, default, default);
    }

    /// <summary>Asks the window to close; safe from any thread.</summary>
    public void Close() => PInvoke.PostMessage(Handle, PInvoke.WM_CLOSE, default, default);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        // An exception must never unwind into user32: log it and carry on (fail-soft).
        try
        {
            if (s_current is { } self && hwnd == self.Handle)
            {
                if (message == InvokeMessage)
                {
                    self.RunPosted();
                    return (LRESULT)0;
                }
                foreach (var handler in self._handlers)
                {
                    if (handler(message, wParam, lParam))
                        return (LRESULT)0;
                }
            }
            if (message == PInvoke.WM_DESTROY)
            {
                PInvoke.PostQuitMessage(0);
                return (LRESULT)0;
            }
        }
        catch (Exception e)
        {
            Log.Error($"window message 0x{message:X}", e);
            return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void RunPosted()
    {
        while (_posted.TryDequeue(out var action))
        {
            // One failing action must not drop the ones queued behind it.
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error("posted action", e);
            }
        }
    }

    public void Dispose()
    {
        if (PInvoke.IsWindow(Handle))
            PInvoke.DestroyWindow(Handle);
        s_current = null;
    }
}
