using System.Runtime.InteropServices;
using DazeControl.Engine.Shell;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

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
        _started = true;
    }

    private void OnResumed() => Raised?.Invoke(new TriggerEvent("power.resume"));

    public void Unload()
    {
        if (!_started)
            return;
        _started = false;
        _resume.Resumed -= OnResumed;
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
    private nint _registration;

    public ResumeMonitor(MessageWindow window)
    {
        _window = window;
        _detector = new ResumeDetector(TimeProvider.System, () => Resumed?.Invoke());
        _window.Add(OnMessage);
    }

    /// <summary>Raised on the message-loop thread once per wake-up.</summary>
    public event Action? Resumed;

    /// <summary>Registers the display-state notification. Fail-soft: without it only classic resume works.</summary>
    public void Start()
    {
        var guid = ConsoleDisplayState;
        _registration = RegisterPowerSettingNotification(_window.Handle, &guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        if (_registration == 0)
            throw new InvalidOperationException($"RegisterPowerSettingNotification (display state) failed: {Marshal.GetLastPInvokeError()}");
    }

    public void Dispose()
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
