using DazeControl.Engine.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

/// <summary>
/// Notification-area icon with a native menu via <c>Shell_NotifyIcon</c>, ported from the OSD (OSD D-009).
/// T0.5 spike: generic icon and Exit only; flows, folders, dark menu and icon variants arrive in T3.2.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = PInvoke.WM_APP + 1;
    private const uint ExitCommand = 1;

    private readonly MessageWindow _window;
    private readonly string _tooltip;
    private readonly uint _taskbarCreated;
    private readonly HICON _icon;
    private bool _added;

    public TrayIcon(MessageWindow window, string tooltip)
    {
        _window = window;
        _tooltip = tooltip;
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        _icon = PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION); // shared icon: never destroyed
        window.Add(OnMessage);
        _added = Add();
        if (!_added)
            Log.Info("tray: Shell_NotifyIcon(NIM_ADD) failed; waiting for TaskbarCreated");
    }

    /// <summary>Exit chosen in the menu. Raised on the message thread.</summary>
    public event Action? ExitRequested;

    private bool Add()
    {
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
            | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        data.uCallbackMessage = CallbackMessage;
        data.szTip = _tooltip;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &data))
            return false;
        // Version 4: WM_CONTEXTMENU for right-click and Shift+F10, anchor in wParam.
        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        return PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &data);
    }

    private void Delete()
    {
        if (!_added)
            return;
        var data = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &data);
        _added = false;
    }

    private NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        hIcon = _icon,
    };

    private bool OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == CallbackMessage)
        {
            // Version 4: LOWORD(lParam) is the event, wParam the anchor in screen coordinates.
            if ((uint)(lParam.Value & 0xFFFF) == PInvoke.WM_CONTEXTMENU)
                ShowMenu((short)(wParam.Value & 0xFFFF), (short)((wParam.Value >> 16) & 0xFFFF));
            return true;
        }
        if (message == PInvoke.WM_CLOSE)
        {
            Delete();
            return false; // DefWindowProc destroys the window, which ends the message loop
        }
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted: the old icon is gone.
            _added = Add();
            Log.Info($"tray: taskbar re-created; icon re-added: {_added}");
            return true;
        }
        return false;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            fixed (char* exit = "Exit")
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, ExitCommand, exit);
            // Required for the menu to close when the user clicks elsewhere (KB135788).
            PInvoke.SetForegroundWindow(_window.Handle);
            var command = PInvoke.TrackPopupMenuEx(menu,
                (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY
                    | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN),
                x, y, _window.Handle, null);
            PInvoke.PostMessage(_window.Handle, PInvoke.WM_NULL, default, default);
            if (command.Value == ExitCommand)
                ExitRequested?.Invoke();
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    public void Dispose() => Delete();
}
