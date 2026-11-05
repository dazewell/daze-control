using DazeControl.Core.Input;
namespace DazeControl.Runtime.Triggers;

/// <summary>
/// Turns button readings into "a chord was pressed" (DESIGN §2.4, D-057). Pure: the source feeds it every change.
/// A chord fires when its buttons were all held together and the first of them is let go, if no other button was
/// held with them: so <c>View+Menu</c> never fires inside <c>View+Menu+A</c>, and the menu opens with the buttons up.
/// Only the first release of a press counts; later releases of the same press never fire.
/// </summary>
public sealed class GamepadChordRecognizer
{
    private HashSet<GamepadButtons> _chords = [];
    private GamepadButtons _held;
    private bool _released;

    /// <summary>Chords to listen for; a press in progress carries over.</summary>
    public void SetChords(IEnumerable<GamepadButtons> chords) => _chords = [.. chords];

    /// <summary>The pad went away or listening stopped: forget the press without firing.</summary>
    public void Reset()
    {
        _held = GamepadButtons.None;
        _released = false;
    }

    /// <summary>Feeds the buttons now held; returns the chord that fired, if any.</summary>
    public GamepadButtons? Update(GamepadButtons held)
    {
        if (held == _held)
            return null;
        GamepadButtons? fired = null;
        if (_held == GamepadButtons.None)
            _released = false;
        else if ((_held & ~held) != GamepadButtons.None && !_released)
        {
            _released = true;
            if (_chords.Contains(_held))
                fired = _held;
        }
        _held = held;
        return fired;
    }
}
