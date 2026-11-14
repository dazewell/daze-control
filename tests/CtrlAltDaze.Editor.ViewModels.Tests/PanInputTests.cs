using CtrlAltDaze.Editor.ViewModels.Canvas;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>Which presses pan the canvas (UX §5.2, F-033).</summary>
public sealed class PanInputTests
{
    [Theory]
    //          left   middle right  space  mouse  touch  → expected
    [InlineData(false, true, false, false, true, false, PanPress.Pan)] // middle-drag
    [InlineData(true, false, false, true, true, false, PanPress.Pan)] // Space + left-drag
    [InlineData(true, false, false, true, false, false, PanPress.Pan)] // Space + pen tip
    [InlineData(false, false, true, false, true, false, PanPress.RightArm)] // right: armed, the drag decides
    [InlineData(false, false, true, true, true, false, PanPress.RightArm)] // Space doesn't change a right press
    [InlineData(true, false, false, false, true, false, PanPress.None)] // plain left: select, drag, marquee
    [InlineData(false, false, true, false, false, false, PanPress.Menu)] // pen barrel button: the menu
    [InlineData(false, false, true, false, false, true, PanPress.Menu)] // touch right press: the menu
    [InlineData(true, false, false, true, false, true, PanPress.None)] // touch: the manipulation pans
    [InlineData(false, false, false, false, true, false, PanPress.None)] // X buttons: nothing
    public void PressesThatPan(bool left, bool middle, bool right, bool space, bool mouse, bool touch, PanPress expected) =>
        Assert.Equal(expected, PanInput.OnPress(left, middle, right, space, mouse, touch));

    [Fact]
    public void MiddleWinsOverAHeldLeft() =>
        Assert.Equal(PanPress.Pan, PanInput.OnPress(left: true, middle: true, right: false, spaceHeld: false, isMouse: true, isTouch: false));

    [Theory]
    [InlineData(false, false, false, false)] // nothing running
    [InlineData(true, false, true, false)] // a live drag, marquee or pan holds the capture: never cut short
    [InlineData(true, true, false, true)] // a right press whose release a canvas button took
    [InlineData(true, true, true, true)] // armed is never captured; if it were, still a leftover at a new press
    [InlineData(true, false, false, true)] // released or lost outside: nothing holds the pointer any more
    public void LeftoverGesturesGiveWayToANewPress(bool running, bool armedOnly, bool captured, bool stale) =>
        Assert.Equal(stale, PanInput.IsStale(running, armedOnly, captured));

    [Theory]
    [InlineData(false, false, true)] // an arrow or Enter with no gesture: scroll to the focused step
    [InlineData(true, false, false)] // Space, key-repeat included, only arms the pan
    [InlineData(true, true, false)] // Space during a Space+left-drag: the camera must not move (F-034)
    [InlineData(false, true, false)] // no key moves the camera under a running gesture
    public void OnlyRealNavigationKeysScrollTheFocusedStepIntoView(bool space, bool gesture, bool reveals) =>
        Assert.Equal(reveals, PanInput.RevealsFocusOnKeyDown(space, gesture));

    [Fact]
    public void SpaceDragTracksThePointerThroughKeyRepeat()
    {
        // Press with Space held, then moves with auto-repeating Space KeyDowns between them. A KeyDown that reveals
        // the focused step shifts the camera by a step-sized jump (the old behaviour, F-034).
        var start = new Camera(10, 20, 1);
        var camera = start;
        for (var i = 1; i <= 20; i++)
        {
            if (PanInput.RevealsFocusOnKeyDown(isSpace: true, gestureRunning: true))
                camera = camera.PanBy(999, 999);
            camera = start.PanBy(i * 7, -i * 3); // the move: press-time camera + pointer delta
            Assert.Equal(new Camera(10 + i * 7, 20 - i * 3, 1), camera);
        }
    }

    [Fact]
    public void AStillRightClickStillOpensItsMenuAndADragSwallowsIt()
    {
        var click = new RightDragPan(4, 4);
        click.Arm(100, 100);
        Assert.False(click.Move(101, 101)); // inside the drag rectangle: still a click
        click.Release();
        Assert.True(click.TakeMenu()); // RightTapped opens the step's or the canvas's menu

        var drag = new RightDragPan(4, 4);
        drag.Arm(100, 100);
        Assert.True(drag.Move(140, 100)); // out of the rectangle: pans
        drag.Release();
        Assert.False(drag.TakeMenu()); // the menu after a pan is swallowed once
        Assert.True(drag.TakeMenu());
    }
}
