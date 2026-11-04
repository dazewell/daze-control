using DazeControl.Runtime.Menus;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

public sealed class MenuModelTests : IDisposable
{
    private readonly ManualTime _time = new();

    public void Dispose() => _time.Dispose();

    private static MenuItem Item(string id, MenuItemState state = MenuItemState.Ready) => new(id, id, state);

    private static List<MenuItem> Items(int n) => [.. Enumerable.Range(1, n).Select(i => Item($"f{i}"))];

    /// <summary>Opens a menu with every pad button up (the first reading), so presses count at once.</summary>
    private MenuModel Menu(int n, MenuLayout layout = MenuLayout.List, string? last = null, int seconds = 30, bool armed = true)
    {
        var menu = new MenuModel(Items(n), layout, TimeSpan.FromSeconds(seconds), _time, last);
        if (armed)
            menu.Pad(Up);
        return menu;
    }

    private static PadReading Press(PadButtons b) => new(b);

    private static readonly PadReading Up = new(PadButtons.None);

    [Fact]
    public void ListMovesAndStopsAtTheEnds()
    {
        using var m = Menu(3);
        Assert.Equal(0, m.Focus);
        m.KeyMove(MenuDirection.Up);
        Assert.Equal(0, m.Focus);
        m.KeyMove(MenuDirection.Down);
        m.KeyMove(MenuDirection.Down);
        m.KeyMove(MenuDirection.Down);
        Assert.Equal(2, m.Focus);
        m.KeyMove(MenuDirection.Left);
        Assert.Equal(2, m.Focus);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(12, 4)]
    public void GridColumns(int count, int columns)
    {
        using var m = Menu(count, MenuLayout.Grid);
        Assert.Equal(columns, m.Columns);
    }

    [Fact]
    public void GridMovesInTwoDimensionsWithoutWrap()
    {
        using var m = Menu(5, MenuLayout.Grid); // 3 columns: [0 1 2] [3 4]
        m.KeyMove(MenuDirection.Right);
        m.KeyMove(MenuDirection.Right);
        m.KeyMove(MenuDirection.Right);
        Assert.Equal(2, m.Focus);
        m.KeyMove(MenuDirection.Down); // column 2 is missing in row 2: last item
        Assert.Equal(4, m.Focus);
        m.KeyMove(MenuDirection.Down);
        Assert.Equal(4, m.Focus);
        m.KeyMove(MenuDirection.Right);
        Assert.Equal(4, m.Focus);
        m.KeyMove(MenuDirection.Left);
        m.KeyMove(MenuDirection.Left);
        m.KeyMove(MenuDirection.Left);
        Assert.Equal(3, m.Focus);
        m.KeyMove(MenuDirection.Up);
        Assert.Equal(0, m.Focus);
        m.KeyMove(MenuDirection.Up);
        Assert.Equal(0, m.Focus);
    }

    [Fact]
    public void DpadRepeatsAfter400MsThenEvery120()
    {
        using var m = Menu(12);
        m.Pad(Press(PadButtons.Down));
        Assert.Equal(1, m.Focus);
        _time.Advance(TimeSpan.FromMilliseconds(399));
        m.Pad(Press(PadButtons.Down));
        Assert.Equal(1, m.Focus);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        m.Pad(Press(PadButtons.Down));
        Assert.Equal(2, m.Focus);
        _time.Advance(TimeSpan.FromMilliseconds(119));
        m.Pad(Press(PadButtons.Down));
        Assert.Equal(2, m.Focus);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        m.Pad(Press(PadButtons.Down));
        Assert.Equal(3, m.Focus);
        m.Pad(Up);
        m.Pad(Press(PadButtons.Down)); // released and pressed again: immediate
        Assert.Equal(4, m.Focus);
    }

    [Fact]
    public void StickHasHysteresis()
    {
        using var m = Menu(12);
        m.Pad(new PadReading(PadButtons.None, 0, -0.4f));
        Assert.Equal(0, m.Focus); // under 0.5: no move
        m.Pad(new PadReading(PadButtons.None, 0, -0.6f));
        Assert.Equal(1, m.Focus);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        m.Pad(new PadReading(PadButtons.None, 0, -0.4f)); // still held (above 0.3): no new move, no early repeat
        Assert.Equal(1, m.Focus);
        m.Pad(new PadReading(PadButtons.None, 0, -0.2f)); // under 0.3: re-armed
        m.Pad(new PadReading(PadButtons.None, 0, -0.4f)); // 0.4 isn't enough to start again
        Assert.Equal(1, m.Focus);
        m.Pad(new PadReading(PadButtons.None, 0, -0.7f));
        Assert.Equal(2, m.Focus);
    }

    [Fact]
    public void StickRightMovesTheGrid()
    {
        using var m = Menu(4, MenuLayout.Grid);
        m.Pad(new PadReading(PadButtons.None, 0.9f, 0.1f));
        Assert.Equal(1, m.Focus);
    }

    [Fact]
    public void ButtonsHeldWhenTheMenuOpensCountOnlyAfterTheyWereReleased()
    {
        using var m = Menu(3, armed: false);
        m.Pad(Press(PadButtons.A | PadButtons.Down)); // both held from the chord/game
        Assert.Null(m.Outcome);
        Assert.Equal(0, m.Focus);
        m.Pad(Up);
        m.Pad(Press(PadButtons.A));
        Assert.Equal("f1", m.Outcome?.FlowId);
        Assert.Equal(MenuCloseReason.Picked, m.Outcome?.Reason);
    }

    [Fact]
    public void BClosesAsCancelled()
    {
        using var m = Menu(3);
        m.Pad(Up);
        m.Pad(Press(PadButtons.B));
        Assert.Equal(new MenuOutcome(null, MenuCloseReason.Cancelled), m.Outcome);
    }

    [Fact]
    public void NumberKeysPickTheFirstNine()
    {
        using var m = Menu(12);
        Assert.False(m.PickNumber(0));
        Assert.False(m.PickNumber(10));
        Assert.Null(m.Outcome);
        Assert.True(m.PickNumber(3));
        Assert.Equal("f3", m.Outcome?.FlowId);
    }

    [Fact]
    public void NumberBeyondTheItemsDoesNothing()
    {
        using var m = Menu(2);
        Assert.False(m.PickNumber(3));
        Assert.Null(m.Outcome);
    }

    [Fact]
    public void ChoiceThatIsOffRejectsButCanBeFocused()
    {
        var rejected = 0;
        using var m = new MenuModel([Item("a"), Item("b", MenuItemState.NeedsFixing), Item("c", MenuItemState.TurnedOff)],
            MenuLayout.List, TimeSpan.FromSeconds(30), _time);
        m.Rejected += () => rejected++;
        m.KeyMove(MenuDirection.Down);
        Assert.Equal(1, m.Focus);
        Assert.False(m.Activate());
        Assert.False(m.PickNumber(3));
        Assert.Equal(2, rejected);
        Assert.Null(m.Outcome);
    }

    [Fact]
    public void RunningChoiceCanBePicked()
    {
        using var m = new MenuModel([Item("a", MenuItemState.Running)], MenuLayout.List, TimeSpan.FromSeconds(30), _time);
        Assert.True(m.Activate());
        Assert.Equal("a", m.Outcome?.FlowId);
    }

    [Fact]
    public void FocusStartsOnTheLastPickElseTheFirstRunnable()
    {
        using var a = Menu(4, last: "f3");
        Assert.Equal(2, a.Focus);
        using var b = Menu(4, last: "gone");
        Assert.Equal(0, b.Focus);
        using var c = new MenuModel([Item("x", MenuItemState.TurnedOff), Item("y")], MenuLayout.List, TimeSpan.FromSeconds(30), _time);
        Assert.Equal(1, c.Focus);
        using var d = new MenuModel([Item("x", MenuItemState.TurnedOff), Item("y", MenuItemState.NeedsFixing)], MenuLayout.List, TimeSpan.FromSeconds(30), _time);
        Assert.Equal(0, d.Focus);
        Assert.True(d.NothingRunnable);
    }

    [Fact]
    public void CloseTimerEndsAsTimedOut()
    {
        using var m = Menu(3, seconds: 30);
        MenuOutcome? seen = null;
        m.Completed += o => seen = o;
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(m.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), m.Remaining);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new MenuOutcome(null, MenuCloseReason.TimedOut), m.Outcome);
        Assert.Equal(m.Outcome, seen);
    }

    [Fact]
    public void AnyInputRestartsTheCloseTimer()
    {
        using var m = Menu(3, seconds: 30);
        _time.Advance(TimeSpan.FromSeconds(25));
        m.KeyMove(MenuDirection.Down);
        _time.Advance(TimeSpan.FromSeconds(25));
        Assert.Null(m.Outcome);
        m.Pad(Press(PadButtons.Down)); // pad input restarts it too
        _time.Advance(TimeSpan.FromSeconds(25));
        Assert.Null(m.Outcome);
        m.Hover(0);
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(m.Outcome);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(MenuCloseReason.TimedOut, m.Outcome?.Reason);
    }

    [Fact]
    public void AnIdlePadReadingDoesNotRestartTheTimer()
    {
        using var m = Menu(3, seconds: 30);
        _time.Advance(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 10; i++)
            m.Pad(Up);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(MenuCloseReason.TimedOut, m.Outcome?.Reason);
    }

    [Fact]
    public void CompletesOnceAndIgnoresInputAfterwards()
    {
        using var m = Menu(3);
        var completed = 0;
        m.Completed += _ => completed++;
        m.Activate();
        m.Cancel();
        m.Stop();
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, completed);
        Assert.Equal("f1", m.Outcome?.FlowId);
        Assert.False(m.KeyMove(MenuDirection.Down));
    }

    [Fact]
    public void StopEndsAsStopped()
    {
        using var m = Menu(3);
        m.Stop();
        Assert.Equal(MenuCloseReason.Stopped, m.Outcome?.Reason);
    }

    [Fact]
    public void SteamEchoKeyWithin100MsOfTheSamePadMoveIsDropped()
    {
        using var m = Menu(12);
        m.Pad(Press(PadButtons.Down));
        Assert.Equal(1, m.Focus);
        _time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(m.KeyMove(MenuDirection.Down)); // Steam's arrow for the same press
        Assert.Equal(1, m.Focus);
        Assert.True(m.KeyMove(MenuDirection.Up)); // another direction is a real key
        Assert.Equal(0, m.Focus);
        m.Pad(Up);
        m.Pad(Press(PadButtons.Down));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(m.KeyMove(MenuDirection.Down)); // 100 ms later: a real key
        Assert.Equal(2, m.Focus);
    }

    [Fact]
    public void HintFollowsTheLastInput()
    {
        using var m = Menu(3);
        Assert.Equal(MenuInput.None, m.LastInput);
        m.KeyMove(MenuDirection.Down);
        Assert.Equal(MenuInput.Keyboard, m.LastInput);
        m.Pad(Press(PadButtons.Down));
        Assert.Equal(MenuInput.Pad, m.LastInput);
        m.Hover(0);
        Assert.Equal(MenuInput.Mouse, m.LastInput);
    }
}
