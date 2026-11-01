using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Interop;
using DazeControl.Runtime.Tray;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DazeControl.Engine.Shell;

/// <summary>
/// Notification-area icon with a native menu via <c>Shell_NotifyIcon</c>, ported from the OSD (OSD D-009). The
/// menu is rendered from <see cref="TrayMenu"/> each time it opens, dark or light like the taskbar; the icon and
/// tooltip follow <see cref="Update"/>. Message thread only.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = PInvoke.WM_APP + 1;
    /// <summary>NIN_KEYSELECT (NIN_SELECT | NINF_KEY): Enter or Space on the focused icon; missing from the CsWin32 metadata.</summary>
    private const uint NinKeySelect = PInvoke.NIN_SELECT | 0x1;
    private const uint FirstCommand = 1;

    private readonly MessageWindow _window;
    private readonly Func<IReadOnlyList<TrayEntry>> _menu;
    private readonly uint _taskbarCreated;
    private readonly Dictionary<TrayGlyphKind, HICON> _icons = [];
    private TrayGlyphKind _kind = TrayGlyphKind.Idle;
    private string _tooltip;
    private bool _added;

    /// <param name="menu">Builds the menu when it opens.</param>
    public TrayIcon(MessageWindow window, string tooltip, Func<IReadOnlyList<TrayEntry>> menu)
    {
        _window = window;
        _tooltip = tooltip;
        _menu = menu;
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        window.Add(OnMessage);
        _added = Add();
        if (!_added)
            Log.Info("tray: Shell_NotifyIcon(NIM_ADD) failed; waiting for TaskbarCreated");
    }

    /// <summary>A flow was picked. Raised on the message thread.</summary>
    public event Action<string>? FlowChosen;

    /// <summary>A fixed item was picked. Raised on the message thread.</summary>
    public event Action<TrayCommand>? CommandChosen;

    /// <summary>Redraws the icon and tooltip if they changed.</summary>
    public void Update(TrayGlyphKind kind, string tooltip)
    {
        if (kind == _kind && tooltip == _tooltip)
            return;
        _kind = kind;
        _tooltip = tooltip;
        if (!_added)
            return;
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        data.szTip = _tooltip;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data);
    }

    /// <summary>A Windows notification from the tray icon (the <c>notify</c> step until the run pop-up, M5).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added)
            return;
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_NONE;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data);
    }

    private HICON Icon(TrayGlyphKind kind)
    {
        if (!_icons.TryGetValue(kind, out var icon))
        {
            icon = TrayGlyph.Create(kind);
            if (icon.IsNull)
                icon = PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION); // shared icon: never destroyed
            else
                _icons[kind] = icon;
        }
        return icon;
    }

    private bool Add()
    {
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
            | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        data.uCallbackMessage = CallbackMessage;
        data.szTip = _tooltip;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &data))
            return false;
        // Version 4: WM_CONTEXTMENU for right-click and Shift+F10, NIN_SELECT for a left click, anchor in wParam.
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
        hIcon = Icon(_kind),
    };

    private bool OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == CallbackMessage)
        {
            // Version 4: LOWORD(lParam) is the event, wParam the anchor in screen coordinates. Left click and
            // keyboard activation (Enter/Space on the focused icon) open the menu too: the tray has nothing else to show.
            var notification = (uint)(lParam.Value & 0xFFFF);
            if (notification is PInvoke.WM_CONTEXTMENU or PInvoke.NIN_SELECT or NinKeySelect)
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

    private static bool TaskbarIsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int light && light != 0;

    private void ShowMenu(int x, int y)
    {
        // Win32 menus are light-only unless uxtheme is told otherwise; follow the taskbar's theme (OSD D-009).
        UndocumentedMethods.SetPreferredAppMode(TaskbarIsLight()
            ? UndocumentedMethods.PreferredAppMode.ForceLight
            : UndocumentedMethods.PreferredAppMode.ForceDark);
        UndocumentedMethods.FlushMenuThemes();

        var menu = PInvoke.CreatePopupMenu();
        var commands = new List<TrayEntry>();
        try
        {
            Append(menu, _menu(), commands);
            // Required for the menu to close when the user clicks elsewhere (KB135788).
            PInvoke.SetForegroundWindow(_window.Handle);
            var command = PInvoke.TrackPopupMenuEx(menu,
                (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY
                    | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN),
                x, y, _window.Handle, null);
            PInvoke.PostMessage(_window.Handle, PInvoke.WM_NULL, default, default);
            var index = command.Value - (int)FirstCommand;
            if (index < 0 || index >= commands.Count)
                return;
            switch (commands[index])
            {
                case TrayFlowItem flow:
                    FlowChosen?.Invoke(flow.FlowId);
                    break;
                case TrayCommandItem fixedItem:
                    CommandChosen?.Invoke(fixedItem.Command);
                    break;
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu); // destroys the submenus too
        }
    }

    private static void Append(HMENU menu, IReadOnlyList<TrayEntry> entries, List<TrayEntry> commands)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case TraySeparator:
                    PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
                    break;
                case TrayFolder folder:
                    var submenu = PInvoke.CreatePopupMenu();
                    Append(submenu, folder.Items, commands);
                    fixed (char* text = Escape(folder.Text))
                        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)submenu.Value, text);
                    break;
                case TrayFlowItem flow:
                    var id = AddCommand(commands, flow);
                    // "\t" right-aligns the shortcut (UX §5.6).
                    var label = Escape(flow.Text) + (flow.Shortcut is null ? "" : "\t" + flow.Shortcut);
                    fixed (char* text = label)
                        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | (flow.Enabled ? 0 : MENU_ITEM_FLAGS.MF_GRAYED), id, text);
                    if (flow.Running)
                        PInvoke.CheckMenuRadioItem(menu, id, id, id, (uint)MENU_ITEM_FLAGS.MF_BYCOMMAND);
                    break;
                case TrayCommandItem item:
                    var flags = MENU_ITEM_FLAGS.MF_STRING | (item.Checked ? MENU_ITEM_FLAGS.MF_CHECKED : 0)
                        | (item.Enabled ? 0 : MENU_ITEM_FLAGS.MF_GRAYED);
                    fixed (char* text = item.Text)
                        PInvoke.AppendMenu(menu, flags, AddCommand(commands, item), text);
                    break;
            }
        }
    }

    private static uint AddCommand(List<TrayEntry> commands, TrayEntry entry)
    {
        commands.Add(entry);
        return FirstCommand + (uint)commands.Count - 1;
    }

    /// <summary>A flow named "Tom &amp; Jerry" must not get a mnemonic underline.</summary>
    private static string Escape(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    public void Dispose()
    {
        Delete();
        foreach (var icon in _icons.Values)
            PInvoke.DestroyIcon(icon);
        _icons.Clear();
    }
}
