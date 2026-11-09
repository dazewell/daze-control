using System.Runtime.InteropServices;
using DazeControl.Engine.Shell;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

/// <summary>
/// <c>session.lock/unlock/logon/logoff</c> (M20, D-107): WTS session notifications for this session, delivered to the
/// engine's message window. Registered only between <see cref="Load"/> and <see cref="Unload"/>; no thread, no timer,
/// no admin. (A logoff usually ends the engine first; the event is raised if the message does arrive.)
/// </summary>
internal sealed partial class SessionSource : ITriggerSource
{
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const int WTS_SESSION_LOGON = 0x5;
    private const int WTS_SESSION_LOGOFF = 0x6;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;

    private readonly MessageWindow _window;
    private volatile bool _registered;

    public SessionSource(MessageWindow window)
    {
        _window = window;
        _window.Add(OnMessage);
    }

    public IReadOnlyList<string> Events { get; } = ["session.lock", "session.unlock", "session.logon", "session.logoff"];

    public event Action<TriggerEvent>? Raised;

    public void Load()
    {
        if (_registered)
            return;
        if (!WTSRegisterSessionNotification(_window.Handle, NOTIFY_FOR_THIS_SESSION))
            throw new InvalidOperationException($"WTSRegisterSessionNotification failed: {Marshal.GetLastPInvokeError()}");
        _registered = true;
    }

    public void Unload()
    {
        if (!_registered)
            return;
        _registered = false;
        _ = WTSUnRegisterSessionNotification(_window.Handle);
    }

    public void Dispose() => Unload();

    private bool OnMessage(uint message, Windows.Win32.Foundation.WPARAM wParam, Windows.Win32.Foundation.LPARAM lParam)
    {
        if (message != WM_WTSSESSION_CHANGE || !_registered)
            return false;
        var name = (int)wParam.Value switch
        {
            WTS_SESSION_LOCK => "session.lock",
            WTS_SESSION_UNLOCK => "session.unlock",
            WTS_SESSION_LOGON => "session.logon",
            WTS_SESSION_LOGOFF => "session.logoff",
            _ => null,
        };
        if (name is not null)
            Raised?.Invoke(new TriggerEvent(name));
        return false; // not exclusive: other handlers and DefWindowProc still see it
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint window, uint flags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint window);
}
