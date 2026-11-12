using DazeControl.Core.Input;

namespace DazeControl.Runtime.Triggers;

/// <summary>One XInput slot's state, as the driver reports it.</summary>
public readonly record struct XInputState(ushort Buttons, byte LeftTrigger, byte RightTrigger, short StickX, short StickY);

/// <summary>The slice of XInput the poller needs (the engine binds <c>xinput1_4</c>; tests fake it).</summary>
public interface IXInput
{
    /// <summary>False when no pad is in the slot (an empty slot is slow to ask, so only connected ones are polled).</summary>
    bool TryGetState(int slot, out XInputState state);

    /// <summary>"Wired", "battery low"…; null when unknown.</summary>
    string? Battery(int slot);
}

/// <summary>A pad the PC has now, for <c>devices gamepads</c>.</summary>
public sealed record PadInfo(int Slot, string? Battery);

/// <summary>
/// XInput polling at 60 Hz, one step at a time, only while a menu is open (DESIGN §3.4, D-056, D-086): polls only the slots a scan found connected, merges them
/// into one <see cref="PadReading"/> and says how long to wait before the next step, or null when no pad is left (the
/// caller parks until an arrival or a resume asks for <see cref="Scan"/>). Pure apart from <see cref="IXInput"/>, so the
/// "no pad → no polling" rule is a unit test. Not thread-safe: one reader thread owns it.
/// </summary>
public sealed class PadPoller(IXInput api)
{
    public const int Slots = 4;
    public static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>The wait while a button is down or the stick is off centre (the engine holds a 1 ms timer tick, so this is real).</summary>
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(8);

    private const byte TriggerOn = 100;
    private const byte TriggerOff = 60;

    // XINPUT_GAMEPAD_* bits; Start is Menu and Back is View on an Xbox pad.
    private static readonly (ushort Bit, GamepadButtons Button)[] Table =
    [
        (0x0001, GamepadButtons.Up), (0x0002, GamepadButtons.Down), (0x0004, GamepadButtons.Left), (0x0008, GamepadButtons.Right),
        (0x0010, GamepadButtons.Menu), (0x0020, GamepadButtons.View), (0x0040, GamepadButtons.LS), (0x0080, GamepadButtons.RS),
        (0x0100, GamepadButtons.LB), (0x0200, GamepadButtons.RB),
        (0x1000, GamepadButtons.A), (0x2000, GamepadButtons.B), (0x4000, GamepadButtons.X), (0x8000, GamepadButtons.Y),
    ];

    private readonly bool[] _connected = new bool[Slots];
    private readonly bool[] _lt = new bool[Slots];
    private readonly bool[] _rt = new bool[Slots];

    /// <summary>Raised on every poll while a pad is on, and once, neutral, when the last one goes.</summary>
    public event Action<PadReading>? Reading;

    /// <summary>Polls so far.</summary>
    public long Polls { get; private set; }

    public bool AnyConnected => _connected.Contains(true);

    /// <summary>Asks every slot once which pads are on; true when there is one.</summary>
    public bool Scan()
    {
        for (var slot = 0; slot < Slots; slot++)
        {
            _connected[slot] = api.TryGetState(slot, out _);
            if (!_connected[slot])
                _lt[slot] = _rt[slot] = false; // a slot that comes back must not inherit a held trigger
        }
        return AnyConnected;
    }

    /// <summary>Reads the pads and raises <see cref="Reading"/>; the delay until the next step, null when no pad is on.</summary>
    public TimeSpan? Step()
    {
        if (!AnyConnected)
            return null;
        var buttons = GamepadButtons.None;
        float x = 0, y = 0;
        for (var slot = 0; slot < Slots; slot++)
        {
            if (!_connected[slot])
                continue;
            Polls++;
            if (!api.TryGetState(slot, out var state))
            {
                _connected[slot] = false;
                _lt[slot] = _rt[slot] = false;
                continue;
            }
            buttons |= Map(state.Buttons);
            _lt[slot] = Trigger(state.LeftTrigger, _lt[slot]);
            _rt[slot] = Trigger(state.RightTrigger, _rt[slot]);
            if (_lt[slot])
                buttons |= GamepadButtons.LT;
            if (_rt[slot])
                buttons |= GamepadButtons.RT;
            var sx = state.StickX / 32768f;
            var sy = state.StickY / 32768f;
            if (Math.Max(Math.Abs(sx), Math.Abs(sy)) > Math.Max(Math.Abs(x), Math.Abs(y)))
                (x, y) = (sx, sy);
        }
        if (!AnyConnected)
        {
            Reading?.Invoke(default); // the last pad went with buttons down: let go of them
            return null;
        }
        Reading?.Invoke(new PadReading(buttons, x, y));
        // Input in progress: poll faster so a press or a stick flick shows within one frame; neutral: back to 60 Hz.
        return buttons != GamepadButtons.None || Math.Max(Math.Abs(x), Math.Abs(y)) > 0.1f ? ActiveInterval : FastInterval;
    }

    /// <summary>The pads now connected (one scan; the reader needn't run).</summary>
    public IReadOnlyList<PadInfo> List()
    {
        List<PadInfo> pads = [];
        for (var slot = 0; slot < Slots; slot++)
        {
            if (api.TryGetState(slot, out _))
                pads.Add(new PadInfo(slot, api.Battery(slot)));
        }
        return pads;
    }

    private static bool Trigger(byte value, bool on) => on ? value >= TriggerOff : value >= TriggerOn;

    private static GamepadButtons Map(ushort bits)
    {
        var buttons = GamepadButtons.None;
        foreach (var (bit, button) in Table)
        {
            if ((bits & bit) != 0)
                buttons |= button;
        }
        return buttons;
    }
}
