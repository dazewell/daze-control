using System.Runtime.InteropServices;
using DazeControl.Core.Input;
using DazeControl.Engine.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DazeControl.Engine.Shell;

/// <summary>
/// Global shortcuts through <c>RegisterHotKey</c> on the engine's message window, one id per chord (OSD
/// D-020, D-022): no hook, no admin; a chord another app owns fails alone and the rest still work.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly MessageWindow _window;
    private readonly HashSet<int> _registered = [];

    public GlobalHotkey(MessageWindow window)
    {
        _window = window;
        window.Add(OnMessage);
    }

    /// <summary>A registered shortcut was pressed (auto-repeat excluded); the argument is its id. Raised on the message thread.</summary>
    public event Action<int>? Pressed;

    /// <summary>Registers <paramref name="hotkey"/> as <paramref name="id"/>; null on success, else why not (user-facing).</summary>
    public string? Register(int id, Hotkey hotkey)
    {
        var modifiers = (HOT_KEY_MODIFIERS)hotkey.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (PInvoke.RegisterHotKey(_window.Handle, id, modifiers, hotkey.Key))
        {
            _registered.Add(id);
            return null;
        }
        var error = Marshal.GetLastPInvokeError();
        Log.Info($"hotkey {hotkey} not registered: error {error}");
        return error == ErrorHotkeyAlreadyRegistered
            ? $"Another app uses {hotkey}. Try another one."
            : $"Couldn't set up {hotkey} (error {error}). Try another one.";
    }

    private bool OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message != PInvoke.WM_HOTKEY || !_registered.Contains((int)wParam.Value))
            return false;
        Pressed?.Invoke((int)wParam.Value);
        return true;
    }

    public void Dispose()
    {
        foreach (var id in _registered)
            PInvoke.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }
}
