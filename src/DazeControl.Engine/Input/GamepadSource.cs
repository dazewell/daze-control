using System.Runtime.InteropServices;
using DazeControl.Engine.Diagnostics;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Input;

/// <summary>
/// XInput (<c>xinput1_4</c>) on the system, bound by hand (DESIGN §3.4, D-056). <c>xinput1_4.dll</c> loads on the
/// first call, so an engine with no chord and no menu never maps it.
/// </summary>
internal sealed unsafe partial class XInputApi : IXInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Battery
    {
        public byte Type, Level;
    }

    public bool TryGetState(int slot, out XInputState state)
    {
        State raw;
        if (XInputGetState((uint)slot, &raw) != 0)
        {
            state = default;
            return false;
        }
        state = new XInputState(raw.Buttons, raw.LeftTrigger, raw.RightTrigger, raw.LeftX, raw.LeftY);
        return true;
    }

    string? IXInput.Battery(int slot)
    {
        Battery info;
        if (XInputGetBatteryInformation((uint)slot, 0 /* BATTERY_DEVTYPE_GAMEPAD */, &info) != 0)
            return null;
        return info.Type switch
        {
            1 => "wired",
            0xFF => null,
            0 => null,
            _ => info.Level switch { 0 => "battery empty", 1 => "battery low", 2 => "battery medium", _ => "battery full" },
        };
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetState(uint index, State* state);

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetBatteryInformation(uint index, byte deviceType, Battery* info);
}

/// <summary>
/// Reads the pads on its own thread, only between <see cref="Start"/> and <see cref="Stop"/> (D-056): 10 Hz, or 60 Hz
/// while a menu is open, only the connected slots; with no pad on the thread waits on an event that a device arrival
/// (or resume) sets, so it costs nothing. Everything is created on the first <see cref="Start"/>.
/// </summary>
internal sealed class GamepadSource : IPadBackend
{
    private readonly PadPoller _poller;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private DeviceArrival? _arrival;
    private volatile bool _stop;
    private volatile bool _fast;

    public GamepadSource(IXInput? api = null)
    {
        _poller = new PadPoller(api ?? new XInputApi());
        _poller.Reading += reading => Reading?.Invoke(reading);
    }

    public event Action<PadReading>? Reading;

    public void SetFast(bool fast) => _fast = fast;

    public IReadOnlyList<PadInfo> List() => _poller.List();

    public void Start()
    {
        if (_thread is not null)
            return;
        _stop = false;
        _arrival = new DeviceArrival(() => _wake.Set());
        _thread = new Thread(Run) { IsBackground = true, Name = "gamepad" };
        _thread.Start();
        Log.Info("gamepad: reading started");
    }

    public void Halt()
    {
        var thread = _thread;
        if (thread is null)
            return;
        _thread = null;
        _stop = true;
        _wake.Set();
        thread.Join(TimeSpan.FromSeconds(2));
        _arrival?.Dispose();
        _arrival = null;
        Log.Info("gamepad: reading stopped");
    }

    public void Dispose()
    {
        Halt();
        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            _poller.Scan();
            while (!_stop)
            {
                var delay = _poller.Step(_fast);
                if (delay is null)
                {
                    // No pad on: nothing to poll until an arrival or a resume.
                    _wake.WaitOne();
                    _poller.Scan();
                }
                else if (_wake.WaitOne(delay.Value))
                {
                    _poller.Scan();
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("gamepad: reader stopped", e);
        }
    }
}
