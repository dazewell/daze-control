using CtrlAltDaze.Editor.ViewModels.Canvas;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>A touchpad's two-finger scroll pans, a mouse wheel zooms, even when Windows reports both as mouse wheel input.</summary>
public sealed class WheelInputTests
{
    [Fact]
    public void AMouseNotchZoomsAndShiftPansSideways()
    {
        var wheel = new WheelInput();
        Assert.Equal(WheelAction.Zoom, wheel.Classify(120, false, false, false, 1_000, false));
        Assert.Equal(WheelAction.Zoom, wheel.Classify(-240, false, false, false, 2_000, false));
        Assert.Equal(WheelAction.PanSideways, wheel.Classify(120, false, false, true, 3_000, false));
        Assert.Equal(WheelAction.PanSideways, wheel.Classify(120, true, false, false, 4_000, false)); // tilt
        Assert.False(wheel.LastWasTouchpad);
    }

    [Fact]
    public void ATouchpadSwipeReportedAsAMouseWheelPans()
    {
        var wheel = new WheelInput();
        Assert.Equal(WheelAction.PanVertical, wheel.Classify(37, false, false, false, 1_000, false));
        Assert.True(wheel.LastWasTouchpad);
        // A whole-notch delta inside the same swipe is still the touchpad's.
        Assert.Equal(WheelAction.PanVertical, wheel.Classify(120, false, false, false, 20_000, false));
        Assert.Equal(WheelAction.PanSideways, wheel.Classify(-18, true, false, false, 30_000, false));
    }

    [Fact]
    public void WindowsSayingTouchpadPansEvenOnAWholeNotch() =>
        Assert.Equal(WheelAction.PanVertical, new WheelInput().Classify(120, false, false, false, 1_000, true));

    [Fact]
    public void TheMouseZoomsAgainOnceTheSwipeIsOver()
    {
        var wheel = new WheelInput();
        wheel.Classify(45, false, false, false, 1_000, false);
        Assert.Equal(WheelAction.Zoom, wheel.Classify(120, false, false, false, 1_000 + WheelInput.StickyMicroseconds + 1, false));
        Assert.False(wheel.LastWasTouchpad);
    }

    [Fact]
    public void CtrlAlwaysZoomsAsAPinchIsCtrlWheel()
    {
        var wheel = new WheelInput();
        Assert.Equal(WheelAction.Zoom, wheel.Classify(12, false, true, false, 1_000, false));
        Assert.Equal(WheelAction.Zoom, wheel.Classify(120, false, true, false, 2_000, true));
        Assert.Equal(WheelAction.Zoom, wheel.Classify(120, false, true, false, 3_000_000, false));
    }
}
