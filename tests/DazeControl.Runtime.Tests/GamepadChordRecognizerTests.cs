using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

public sealed class GamepadChordRecognizerTests
{
    private const PadButtons ViewMenu = PadButtons.View | PadButtons.Menu;
    private readonly GamepadChordRecognizer _r = new();

    public GamepadChordRecognizerTests() => _r.SetChords([ViewMenu, PadButtons.LB | PadButtons.RB | PadButtons.Y]);

    [Fact]
    public void FiresOnTheFirstReleaseOfTheExactSet()
    {
        Assert.Null(_r.Update(PadButtons.View));
        Assert.Null(_r.Update(ViewMenu));
        Assert.Equal(ViewMenu, _r.Update(PadButtons.Menu));
        Assert.Null(_r.Update(PadButtons.None));
    }

    [Fact]
    public void FiresWhenBothAreReleasedTogether()
    {
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(PadButtons.None));
    }

    [Fact]
    public void NeverFiresInsideALongerChord()
    {
        _r.Update(ViewMenu);
        Assert.Null(_r.Update(ViewMenu | PadButtons.A));
        Assert.Null(_r.Update(ViewMenu)); // A let go first: the held set was View+Menu+A
        Assert.Null(_r.Update(PadButtons.None));
    }

    [Fact]
    public void ReleasingOneThenPressingAnotherDoesNotFireTwice()
    {
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(PadButtons.View));
        Assert.Null(_r.Update(PadButtons.View | PadButtons.A));
        Assert.Null(_r.Update(PadButtons.None));
    }

    [Fact]
    public void NotAChordOrOnlyPartOfOneNeverFires()
    {
        _r.Update(PadButtons.View);
        Assert.Null(_r.Update(PadButtons.None));
        _r.Update(PadButtons.LB | PadButtons.RB);
        Assert.Null(_r.Update(PadButtons.None));
    }

    [Fact]
    public void FiresAgainOnTheNextPress()
    {
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(PadButtons.None));
        _r.Update(ViewMenu);
        Assert.Equal(ViewMenu, _r.Update(PadButtons.None));
    }

    [Fact]
    public void ThreeButtonChord()
    {
        var chord = PadButtons.LB | PadButtons.RB | PadButtons.Y;
        _r.Update(PadButtons.LB);
        _r.Update(PadButtons.LB | PadButtons.RB);
        _r.Update(chord);
        Assert.Equal(chord, _r.Update(PadButtons.LB | PadButtons.RB));
    }

    [Fact]
    public void ResetForgetsAPressInProgress()
    {
        _r.Update(ViewMenu);
        _r.Reset();
        Assert.Null(_r.Update(PadButtons.None));
    }

    [Fact]
    public void UnchangedReadingsAreIgnored()
    {
        _r.Update(ViewMenu);
        Assert.Null(_r.Update(ViewMenu));
        Assert.Equal(ViewMenu, _r.Update(PadButtons.None));
    }
}
