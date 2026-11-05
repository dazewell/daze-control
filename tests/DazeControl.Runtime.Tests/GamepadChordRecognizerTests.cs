using DazeControl.Core.Input;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

public sealed class GamepadChordRecognizerTests
{
    private const GamepadButtons ViewMenu = GamepadButtons.View | GamepadButtons.Menu;
    private readonly GamepadChordRecognizer _r = new();

    public GamepadChordRecognizerTests() => _r.SetChords([ViewMenu, GamepadButtons.LB | GamepadButtons.RB | GamepadButtons.Y]);

    [Fact]
    public void FiresOnTheFirstReleaseOfTheExactSet()
    {
        Assert.Null(_r.Update(GamepadButtons.View));
        Assert.Null(_r.Update(ViewMenu));
        Assert.Equal(ViewMenu, _r.Update(GamepadButtons.Menu));
        Assert.Null(_r.Update(GamepadButtons.None));
    }

    [Fact]
    public void FiresWhenBothAreReleasedTogether()
    {
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(GamepadButtons.None));
    }

    [Fact]
    public void NeverFiresInsideALongerChord()
    {
        _r.Update(ViewMenu);
        Assert.Null(_r.Update(ViewMenu | GamepadButtons.A));
        Assert.Null(_r.Update(ViewMenu)); // A let go first: the held set was View+Menu+A
        Assert.Null(_r.Update(GamepadButtons.None));
    }

    [Fact]
    public void ReleasingOneThenPressingAnotherDoesNotFireTwice()
    {
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(GamepadButtons.View));
        Assert.Null(_r.Update(GamepadButtons.View | GamepadButtons.A));
        Assert.Null(_r.Update(GamepadButtons.None));
    }

    [Fact]
    public void NotAChordOrOnlyPartOfOneNeverFires()
    {
        _r.Update(GamepadButtons.View);
        Assert.Null(_r.Update(GamepadButtons.None));
        _r.Update(GamepadButtons.LB | GamepadButtons.RB);
        Assert.Null(_r.Update(GamepadButtons.None));
    }

    [Fact]
    public void FiresAgainOnTheNextPress()
    {
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(GamepadButtons.None));
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(GamepadButtons.None));
    }

    [Fact]
    public void ThreeButtonChord()
    {
        var chord = GamepadButtons.LB | GamepadButtons.RB | GamepadButtons.Y;
        _r.Update(GamepadButtons.LB);
        _r.Update(GamepadButtons.LB | GamepadButtons.RB);
        _r.Update(chord);
        Assert.Equal(chord, _r.Update(GamepadButtons.LB | GamepadButtons.RB));
    }

    [Fact]
    public void ResetForgetsAPressInProgress()
    {
        _r.Update(ViewMenu);
        _r.Reset();
        Assert.Null(_r.Update(GamepadButtons.None));
    }

    [Fact]
    public void UnchangedReadingsAreIgnored()
    {
        _r.Update(ViewMenu);
        Assert.Null(_r.Update(ViewMenu));
        Assert.Equal(ViewMenu, _r.Update(GamepadButtons.None));
    }
}
