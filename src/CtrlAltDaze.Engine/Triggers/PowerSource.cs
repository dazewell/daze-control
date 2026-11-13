using System.Runtime.InteropServices;
using CtrlAltDaze.Engine.Shell;
using CtrlAltDaze.Runtime.Triggers;

namespace CtrlAltDaze.Engine.Triggers;

/// <summary>
/// <c>power.resume</c> (from <see cref="ResumeMonitor"/>: classic resume message or, on Modern Standby, the display
/// coming back) and <c>power.ac/battery</c> (a power-setting notification for the power source, registered only while
/// started). The notification sends the current state once on registration; that is the baseline, not an event, so only
/// a change raises. No thread, no timer, no admin.
/// </summary>
internal sealed unsafe partial class PowerSource : ITriggerSource
{
    private const uint WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid AcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

    private readonly MessageWindow _window;
    private nint _registration;
    private volatile bool _started;
    private int _lastSource = -1;

    private readonly ResumeMonitor _resume;

    public PowerSource(MessageWindow window, ResumeMonitor resume)
    {
        _window = window;
        _resume = resume;
        _window.Add(OnMessage);
    }

    public IReadOnlyList<string> Events { get; } = ["power.resume", "power.ac", "power.battery"];

    public event Action<TriggerEvent>? Raised;

    public void Load()
    {
        if (_started)
            return;
        var guid = AcDcPowerSource;
        _lastSource = -1;
        _registration = RegisterPowerSettingNotification(_window.Handle, &guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        if (_registration == 0)
            throw new InvalidOperationException($"RegisterPowerSettingNotification failed: {Marshal.GetLastPInvokeError()}");
        _resume.Resumed += OnResumed;
        try
        {
            _resume.Retain(this);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Without the display-state signal only classic resume works; the AC/battery triggers still do.
            _ = e; // fail-soft
        }
        _started = true;
    }

    private void OnResumed() => Raised?.Invoke(new TriggerEvent("power.resume"));

    public void Unload()
    {
        if (!_started)
            return;
        _started = false;
        _resume.Resumed -= OnResumed;
        _resume.Release(this);
        if (_registration != 0)
            _ = UnregisterPowerSettingNotification(_registration);
        _registration = 0;
    }

    public void Dispose() => Unload();

    private bool OnMessage(uint message, Windows.Win32.Foundation.WPARAM wParam, Windows.Win32.Foundation.LPARAM lParam)
    {
        if (message != WM_POWERBROADCAST || !_started)
            return false;
        switch ((int)wParam.Value)
        {
            case PBT_POWERSETTINGCHANGE when lParam.Value != 0:
                // POWERBROADCAST_SETTING: GUID, DWORD DataLength, then the data (a DWORD for the power source).
                var setting = (byte*)lParam.Value;
                if (*(Guid*)setting == AcDcPowerSource && *(uint*)(setting + 16) >= 4)
                    OnPowerSource(*(int*)(setting + 20));
                break;
        }
        return false;
    }

    /// <summary>0 = AC, 1 = battery, 2 = short-term (UPS, counted as AC). Only a change raises.</summary>
    private void OnPowerSource(int source)
    {
        var onBattery = source == 1 ? 1 : 0;
        var previous = Interlocked.Exchange(ref _lastSource, onBattery);
        if (previous == -1 || previous == onBattery)
            return;
        Raised?.Invoke(new TriggerEvent(onBattery == 1 ? "power.battery" : "power.ac"));
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterPowerSettingNotification(nint recipient, Guid* powerSetting, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint handle);
}

/// <summary>
/// <c>display.off/on</c> (console display state) and <c>session.away/back</c> (user presence), T24.4: two power-setting
/// notifications on the engine's window, registered only between Load and Unload. Windows reports the current value on
/// registration (the baseline, swallowed by <see cref="ScreenAwayDetector"/>); "away" follows the screen-off time, so
/// there is no timer here. Our own "Turn screens off" step raises <c>display.off</c> like any other cause. No thread, no admin.
/// </summary>
internal sealed unsafe partial class ScreenSource : ITriggerSource
{
    private const uint WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid UserPresence = new("3c0f4548-c03f-4c4d-b9f2-237ede686376");

    private readonly MessageWindow _window;
    private readonly ScreenAwayDetector _detector = new();
    private nint _display, _presence;
    private volatile bool _started;

    public ScreenSource(MessageWindow window)
    {
        _window = window;
        _window.Add(OnMessage);
    }

    public IReadOnlyList<string> Events { get; } = ["display.off", "display.on", "session.away", "session.back"];

    public event Action<TriggerEvent>? Raised;

    public void Load()
    {
        if (_started)
            return;
        _detector.Reset();
        var display = ConsoleDisplayState;
        var presence = UserPresence;
        _display = RegisterPowerSettingNotification(_window.Handle, &display, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        _presence = RegisterPowerSettingNotification(_window.Handle, &presence, 0);
        if (_display == 0 && _presence == 0)
            throw new InvalidOperationException($"RegisterPowerSettingNotification failed: {Marshal.GetLastPInvokeError()}");
        _started = true; // one of the two missing only silences its own events
    }

    public void Unload()
    {
        if (!_started)
            return;
        _started = false;
        if (_display != 0)
            _ = UnregisterPowerSettingNotification(_display);
        if (_presence != 0)
            _ = UnregisterPowerSettingNotification(_presence);
        _display = _presence = 0;
    }

    public void Dispose() => Unload();

    private bool OnMessage(uint message, Windows.Win32.Foundation.WPARAM wParam, Windows.Win32.Foundation.LPARAM lParam)
    {
        if (message != WM_POWERBROADCAST || !_started || (int)wParam.Value != PBT_POWERSETTINGCHANGE || lParam.Value == 0)
            return false;
        // POWERBROADCAST_SETTING: GUID, DWORD DataLength, then the data (a DWORD for both).
        var setting = (byte*)lParam.Value;
        if (*(uint*)(setting + 16) < 4)
            return false;
        var guid = *(Guid*)setting;
        var value = *(int*)(setting + 20);
        var name = guid == ConsoleDisplayState ? _detector.Display(value) : guid == UserPresence ? _detector.Presence(value) : null;
        if (name is not null)
            Raised?.Invoke(new TriggerEvent(name));
        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterPowerSettingNotification(nint recipient, Guid* powerSetting, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint handle);
}

/// <summary>
/// The "PC woke up" signal for <c>power.resume</c> and the schedules' missed-run re-arm. Registered once for the
/// engine's life (a notification, no thread or timer): the console display state (the only wake signal Modern Standby
/// gives apps; Kernel-Power 506/507) and WM_POWERBROADCAST resume (classic S3). Decisions are in <see cref="ResumeDetector"/>.
/// </summary>
internal sealed unsafe partial class ResumeMonitor : IDisposable
{
    private const uint WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMESUSPEND = 0x7;
    private const int PBT_APMRESUMEAUTOMATIC = 0x12;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private readonly MessageWindow _window;
    private readonly ResumeDetector _detector;
    private readonly HashSet<object> _owners = [];
    private nint _registration;

    public ResumeMonitor(MessageWindow window)
    {
        _window = window;
        _detector = new ResumeDetector(TimeProvider.System, () => Resumed?.Invoke());
        _window.Add(OnMessage);
    }

    /// <summary>Raised on the message-loop thread once per wake-up.</summary>
    public event Action? Resumed;

    /// <summary>
    /// An owner (the power source, the schedules) needs the signal: the first one registers the display-state
    /// notification. Fail-soft: without it only classic resume works. Nobody needs it = nothing registered.
    /// </summary>
    public void Retain(object owner)
    {
        lock (_owners)
        {
            if (!_owners.Add(owner) || _owners.Count > 1 || _registration != 0)
                return;
            var guid = ConsoleDisplayState;
            _registration = RegisterPowerSettingNotification(_window.Handle, &guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
            if (_registration == 0)
            {
                _owners.Remove(owner); // so the next Retain tries again
                throw new InvalidOperationException($"RegisterPowerSettingNotification (display state) failed: {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    /// <summary>The owner no longer needs it; the last one out unregisters.</summary>
    public void Release(object owner)
    {
        lock (_owners)
        {
            if (_owners.Remove(owner) && _owners.Count == 0)
                Unregister();
        }
    }

    public void Dispose()
    {
        lock (_owners)
        {
            _owners.Clear();
            Unregister();
        }
    }

    private void Unregister()
    {
        if (_registration != 0)
            _ = UnregisterPowerSettingNotification(_registration);
        _registration = 0;
    }

    private bool OnMessage(uint message, Windows.Win32.Foundation.WPARAM wParam, Windows.Win32.Foundation.LPARAM lParam)
    {
        if (message != WM_POWERBROADCAST)
            return false;
        switch ((int)wParam.Value)
        {
            case PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND:
                _detector.ResumeAutomatic();
                break;
            case PBT_POWERSETTINGCHANGE when lParam.Value != 0:
                var setting = (byte*)lParam.Value;
                if (*(Guid*)setting == ConsoleDisplayState && *(uint*)(setting + 16) >= 4)
                    _detector.DisplayState(*(int*)(setting + 20));
                break;
        }
        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterPowerSettingNotification(nint recipient, Guid* powerSetting, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint handle);
}

/// <summary><c>only: onBattery</c> / <c>onAC</c>: <c>GetSystemPowerStatus</c> when the trigger fires, nothing resident.</summary>
internal sealed partial class PowerCondition(string id, bool wantBattery) : ITriggerCondition
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    public string Id => id;

    public static IReadOnlyList<ITriggerCondition> All() => [new PowerCondition("onBattery", true), new PowerCondition("onAC", false)];

    public Task<bool> HoldsAsync(CancellationToken cancellationToken)
    {
        if (!GetSystemPowerStatus(out var status) || status.AcLineStatus == 255)
            throw new InvalidOperationException("The power source isn't known.");
        var onBattery = status.AcLineStatus == 0;
        return Task.FromResult(onBattery == wantBattery);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);
}
