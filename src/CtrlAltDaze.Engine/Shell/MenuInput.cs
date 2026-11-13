using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CtrlAltDaze.Engine.Diagnostics;
using CtrlAltDaze.Runtime.Menus;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CtrlAltDaze.Engine.Shell;

/// <summary>Is a DXGI full-screen game in front? (D-055, X-010; 3 for Fullscreen Optimizations too.)</summary>
internal static class FullScreen
{
    public static bool OverGame()
    {
        // S_OK with QUNS_RUNNING_D3D_FULL_SCREEN; any failure means "no idea": show the menu.
        return PInvoke.SHQueryUserNotificationState(out var state).Succeeded
            && state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
    }
}

/// <summary>
/// The keys an open menu owns (DESIGN §3.3): <c>RegisterHotKey</c> on the engine's message window, only between
/// <see cref="Enable"/> and <see cref="Disable"/>. A key another app holds just doesn't work (one log line). Registered
/// without MOD_NOREPEAT so a held arrow repeats. Message thread only.
/// </summary>
internal sealed class MenuKeys : IDisposable
{
    private const int FirstId = 0xA000; // app ids are 0x0000–0xBFFF; the shortcut table counts up from 1
    private readonly MessageWindow _window;
    private readonly List<int> _registered = [];
    private Action<MenuKeyAction>? _onKey;

    public MenuKeys(MessageWindow window)
    {
        _window = window;
        window.Add(OnMessage);
    }

    public bool Enabled => _onKey is not null;

    public void Enable(Action<MenuKeyAction> onKey)
    {
        Disable();
        _onKey = onKey;
        List<string> failed = [];
        var keys = MenuKeyMap.All;
        for (var i = 0; i < keys.Count; i++)
        {
            var (key, modifiers, _) = keys[i];
            if (PInvoke.RegisterHotKey(_window.Handle, FirstId + i, (HOT_KEY_MODIFIERS)modifiers, (uint)key))
                _registered.Add(FirstId + i);
            else
                failed.Add($"0x{key:X2}{(modifiers != 0 ? "+Shift" : "")}");
        }
        if (failed.Count > 0)
            Log.Info($"menu: keys held by another app, they won't work in the menu: {string.Join(", ", failed)}");
    }

    public void Disable()
    {
        _onKey = null;
        foreach (var id in _registered)
            PInvoke.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    private bool OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        var id = (int)wParam.Value;
        if (message != PInvoke.WM_HOTKEY || _onKey is not { } onKey || !_registered.Contains(id))
            return false;
        onKey(MenuKeyMap.All[id - FirstId].Action);
        return true;
    }

    public void Dispose() => Disable();
}

/// <summary>
/// A pass-through <c>WH_MOUSE_LL</c> hook, installed only while a menu is open (DESIGN §3.3): clicks outside the menu
/// close it, the wheel over it scrolls. Never swallows an event and returns at once; the handler only posts. It is
/// called on the installing (message) thread, so install and remove there.
/// </summary>
internal sealed unsafe class MenuMouseHook : IDisposable
{
    private static MenuMouseHook? s_current;
    private HHOOK _hook;
    private Action<uint, int, int, int>? _onEvent;

    /// <param name="onEvent">(message, x, y, wheel delta) for button-down and wheel events, screen pixels; must not block.</param>
    public void Start(Action<uint, int, int, int> onEvent)
    {
        Stop();
        _onEvent = onEvent;
        s_current = this;
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &Proc,
            (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value, 0);
        if (_hook == HHOOK.Null)
            Log.Warn($"menu: mouse hook not installed, error {Marshal.GetLastPInvokeError()}: a click outside won't close it");
    }

    public void Stop()
    {
        if (_hook != HHOOK.Null)
            PInvoke.UnhookWindowsHookEx(_hook);
        _hook = HHOOK.Null;
        _onEvent = null;
        if (ReferenceEquals(s_current, this))
            s_current = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Proc(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (code >= 0 && s_current is { } self && self._onEvent is { } onEvent)
            {
                var message = (uint)wParam.Value;
                if (message is PInvoke.WM_LBUTTONDOWN or PInvoke.WM_RBUTTONDOWN or PInvoke.WM_MBUTTONDOWN or PInvoke.WM_XBUTTONDOWN
                    or PInvoke.WM_MOUSEWHEEL)
                {
                    var data = (MSLLHOOKSTRUCT*)lParam.Value;
                    onEvent(message, data->pt.X, data->pt.Y, (short)(data->mouseData >> 16));
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("menu: mouse hook", e); // never unwind into user32
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    public void Dispose() => Stop();
}
