using DazeControl.Core.Input;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Menus;

/// <summary>What a choice's caption says (DESIGN §2.1); only Ready and Running can be picked.</summary>
public enum MenuItemState { Ready, Running, NeedsFixing, TurnedOff }

/// <param name="FlowId">The flow this choice runs.</param>
/// <param name="Name">Shown name.</param>
/// <param name="State">Caption state.</param>
public sealed record MenuItem(string FlowId, string Name, MenuItemState State)
{
    public bool CanRun => State is MenuItemState.Ready or MenuItemState.Running;
}

public enum MenuLayout { List, Grid }

public enum MenuDirection { Up, Down, Left, Right, Next, Previous, First, Last }

public enum MenuCloseReason { Picked, Cancelled, TimedOut, Stopped }

/// <summary>How the menu ended: <see cref="FlowId"/> is the pick (Reason Picked), else Reason says why not.</summary>
public sealed record MenuOutcome(string? FlowId, MenuCloseReason Reason);

/// <summary>The input the hint row follows (DESIGN §4.1).</summary>
public enum MenuInput { None, Pad, Keyboard, Mouse }

/// <summary>
/// The state of one open menu (DESIGN §2.1, §3.4, §4.3). Pure: the window feeds it controller readings, keys and
/// clicks and draws <see cref="Focus"/>; the only clock is the injected <see cref="TimeProvider"/> (close timer, pad
/// repeat: readings arrive at 60 Hz while a menu is open, so repeat needs no timer). Thread-safe, because the close
/// timer fires on a pool thread; events are raised outside the lock, <see cref="Completed"/> once.
/// </summary>
public sealed class MenuModel : IDisposable
{
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>A key move this soon after the same pad move is Steam Input's echo of it (DESIGN §3.4).</summary>
    public static readonly TimeSpan SteamEcho = TimeSpan.FromMilliseconds(100);

    private const float StickOn = 0.5f;
    private const float StickOff = 0.3f;

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _closeAfter;
    private readonly ITimer _timer;
    private long _deadline;
    private bool _done;

    private GamepadButtons _armed;
    private GamepadButtons _padHeld;
    private MenuDirection? _stickDir;
    private MenuDirection? _padDir;
    private long _nextRepeat;
    private MenuDirection? _lastPadMove;
    private long _lastPadMoveAt;

    private bool _rejected;
    private bool _changed;
    private MenuOutcome? _completed;

    public MenuModel(IReadOnlyList<MenuItem> items, MenuLayout layout, TimeSpan closeAfter, TimeProvider time,
        string? lastPickedFlowId = null)
    {
        Items = items;
        Layout = layout;
        _time = time;
        _closeAfter = closeAfter;
        Columns = layout == MenuLayout.List ? 1 : ColumnsFor(items.Count);
        Focus = InitialFocus(items, lastPickedFlowId);
        _deadline = time.GetTimestamp() + ToTicks(closeAfter);
        _timer = time.CreateTimer(OnTimer, null, closeAfter, Timeout.InfiniteTimeSpan);
    }

    public IReadOnlyList<MenuItem> Items { get; }
    public MenuLayout Layout { get; }

    /// <summary>1 for a list; DESIGN §4.1 for a grid (1–3 items one row, 4 → 2, 5–9 → 3, 10–12 → 4).</summary>
    public int Columns { get; }

    public int Focus { get; private set; }
    public MenuInput LastInput { get; private set; }

    /// <summary>Null while the menu is open.</summary>
    public MenuOutcome? Outcome { get; private set; }

    /// <summary>Nothing can run: the window shows the reasons and "Nothing here can run right now" (DESIGN §4.4).</summary>
    public bool NothingRunnable => Items.All(i => !i.CanRun);

    /// <summary>Time left on the close timer (the last 5 s show "Closing in 5 s").</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
                return TimeSpan.FromSeconds(Math.Max(0, _deadline - _time.GetTimestamp()) / (double)_time.TimestampFrequency);
        }
    }

    /// <summary>Focus or <see cref="LastInput"/> changed.</summary>
    public event Action? Changed;

    /// <summary>A choice that is off was activated: the window shakes it.</summary>
    public event Action? Rejected;

    /// <summary>The menu ended; <see cref="Outcome"/> says how.</summary>
    public event Action<MenuOutcome>? Completed;

    public static int ColumnsFor(int count) => count switch { <= 3 => Math.Max(1, count), 4 => 2, <= 9 => 3, _ => 4 };

    private static int InitialFocus(IReadOnlyList<MenuItem> items, string? last)
    {
        if (last is not null)
        {
            for (var i = 0; i < items.Count; i++)
                if (items[i].FlowId == last)
                    return i;
        }
        for (var i = 0; i < items.Count; i++)
            if (items[i].CanRun)
                return i;
        return 0;
    }

    private long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    /// <summary>Keyboard or Steam-layout move; false when dropped as Steam's echo of a pad move or when the menu is over.</summary>
    public bool KeyMove(MenuDirection direction) => Do(() =>
    {
        if (_lastPadMove == direction && _time.GetTimestamp() - _lastPadMoveAt < ToTicks(SteamEcho))
            return false;
        LastInput = MenuInput.Keyboard;
        MoveFocus(direction);
        return true;
    });

    /// <summary>Mouse hover over a choice.</summary>
    public bool Hover(int index) => Do(() =>
    {
        LastInput = MenuInput.Mouse;
        if (index < 0 || index >= Items.Count)
            return false;
        Focus = index;
        return true;
    });

    /// <summary>Enter/Space (keyboard) or a click on the focused choice.</summary>
    public bool Activate(MenuInput source = MenuInput.Keyboard) => Do(() =>
    {
        LastInput = source;
        return ActivateFocus();
    });

    /// <summary>Keys 1–9 pick directly.</summary>
    public bool PickNumber(int number) => Do(() =>
    {
        LastInput = MenuInput.Keyboard;
        if (number < 1 || number > Math.Min(9, Items.Count))
            return false;
        Focus = number - 1;
        return ActivateFocus();
    });

    /// <summary>Esc, a click outside.</summary>
    public bool Cancel() => Do(() =>
    {
        Finish(new MenuOutcome(null, MenuCloseReason.Cancelled));
        return true;
    });

    /// <summary>The trigger's flow was stopped.</summary>
    public bool Stop() => Do(() =>
    {
        Finish(new MenuOutcome(null, MenuCloseReason.Stopped));
        return true;
    });

    /// <summary>
    /// A controller reading. A button counts only after it was seen up since the menu opened; the D-pad and the
    /// stick move once, repeat after 400 ms, then every 120 ms. A (new press) picks, B closes.
    /// </summary>
    public void Pad(PadReading reading) => Do(() =>
    {
        var now = _time.GetTimestamp();
        _armed |= ~reading.Buttons;
        var held = reading.Buttons & _armed;
        var pressed = held & ~_padHeld;
        var input = pressed != GamepadButtons.None;
        _padHeld = held;

        _stickDir = Stick(reading.StickX, reading.StickY);
        var dir = DpadDirection(held) ?? _stickDir;
        if (dir is null)
            _padDir = null;
        else if (dir != _padDir)
        {
            _padDir = dir;
            _nextRepeat = now + ToTicks(RepeatDelay);
            PadMove(dir.Value, now);
            input = true;
        }
        else if (now >= _nextRepeat)
        {
            _nextRepeat = now + ToTicks(RepeatInterval);
            PadMove(dir.Value, now);
            input = true;
        }

        if (input)
            LastInput = MenuInput.Pad;
        if ((pressed & GamepadButtons.A) != 0)
            ActivateFocus();
        else if ((pressed & GamepadButtons.B) != 0)
            Finish(new MenuOutcome(null, MenuCloseReason.Cancelled));
        return input;
    }, restartTimer: false);

    private static MenuDirection? DpadDirection(GamepadButtons held) =>
        (held & GamepadButtons.Up) != 0 ? MenuDirection.Up
        : (held & GamepadButtons.Down) != 0 ? MenuDirection.Down
        : (held & GamepadButtons.Left) != 0 ? MenuDirection.Left
        : (held & GamepadButtons.Right) != 0 ? MenuDirection.Right
        : null;

    /// <summary>Past 0.5 moves; stays on until back under 0.3 (hysteresis), so a stick resting near the edge doesn't flutter.</summary>
    private MenuDirection? Stick(float x, float y)
    {
        var magnitude = Math.Max(Math.Abs(x), Math.Abs(y));
        if (_stickDir is not null)
            return magnitude < StickOff ? null : _stickDir;
        if (magnitude < StickOn)
            return null;
        return Math.Abs(x) >= Math.Abs(y)
            ? x > 0 ? MenuDirection.Right : MenuDirection.Left
            : y > 0 ? MenuDirection.Up : MenuDirection.Down;
    }

    private void PadMove(MenuDirection direction, long now)
    {
        _lastPadMove = direction;
        _lastPadMoveAt = now;
        MoveFocus(direction);
    }

    private void MoveFocus(MenuDirection direction)
    {
        var count = Items.Count;
        if (count == 0)
            return;
        var target = direction switch
        {
            MenuDirection.First => 0,
            MenuDirection.Last => count - 1,
            MenuDirection.Next => Focus + 1,
            MenuDirection.Previous => Focus - 1,
            MenuDirection.Up => Focus - Columns,
            MenuDirection.Down => Below(),
            MenuDirection.Left => Columns > 1 && Focus % Columns > 0 ? Focus - 1 : Focus,
            _ => Columns > 1 && Focus % Columns < Columns - 1 ? Focus + 1 : Focus,
        };
        Focus = Math.Clamp(target, 0, count - 1);

        // A partly filled last row: down from a column it lacks lands on its last item.
        int Below()
        {
            var next = Focus + Columns;
            return next < count ? next : Focus / Columns < (count - 1) / Columns ? count - 1 : Focus;
        }
    }

    private bool ActivateFocus()
    {
        if (Items.Count == 0 || !Items[Focus].CanRun)
        {
            _rejected = true;
            return false;
        }
        Finish(new MenuOutcome(Items[Focus].FlowId, MenuCloseReason.Picked));
        return true;
    }

    /// <summary>Runs one input under the lock, restarts the close timer when the input counted, raises events after the lock.</summary>
    private bool Do(Func<bool> action, bool restartTimer = true)
    {
        bool result;
        lock (_lock)
        {
            if (_done)
                return false;
            var (focus, input) = (Focus, LastInput);
            result = action();
            _changed = Focus != focus || LastInput != input;
            if (!_done && (restartTimer || result))
            {
                _deadline = _time.GetTimestamp() + ToTicks(_closeAfter);
                _timer.Change(_closeAfter, Timeout.InfiniteTimeSpan);
            }
        }
        Raise();
        return result;
    }

    private void Finish(MenuOutcome outcome)
    {
        _done = true;
        Outcome = outcome;
        _completed = outcome;
        _timer.Dispose();
    }

    private void OnTimer(object? state)
    {
        lock (_lock)
        {
            // An input may have restarted the deadline after this callback was queued.
            if (_done || _time.GetTimestamp() < _deadline)
                return;
            Finish(new MenuOutcome(null, MenuCloseReason.TimedOut));
        }
        Raise();
    }

    private void Raise()
    {
        MenuOutcome? completed;
        bool changed, rejected;
        lock (_lock)
        {
            (completed, _completed) = (_completed, null);
            (changed, _changed) = (_changed, false);
            (rejected, _rejected) = (_rejected, false);
        }
        if (rejected)
            Rejected?.Invoke();
        if (changed)
            Changed?.Invoke();
        if (completed is not null)
            Completed?.Invoke(completed);
    }

    public void Dispose() => _timer.Dispose();
}
