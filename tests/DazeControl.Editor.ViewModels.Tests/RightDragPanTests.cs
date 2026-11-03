using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Right-drag pans the canvas; a right click that stays inside the drag rectangle opens the menu (D-069).</summary>
public sealed class RightDragPanTests
{
    // 4 × 4 px at 96 DPI: the Windows default SM_CXDRAG / SM_CYDRAG, centred on the press.
    private static RightDragPan Gesture() => new(4, 4);

    [Fact]
    public void StillClickOpensTheMenu()
    {
        var pan = Gesture();
        pan.Arm(100, 100);
        Assert.False(pan.Move(101, 99));
        pan.Release();
        Assert.True(pan.TakeMenu());
    }

    [Fact]
    public void MoveInsideTheRectangleStaysArmed()
    {
        var pan = Gesture();
        pan.Arm(100, 100);
        Assert.False(pan.Move(102, 102)); // on the edge is still inside
        Assert.True(pan.IsArmed);
        Assert.False(pan.IsPanning);
    }

    [Theory]
    [InlineData(102.5, 100)]
    [InlineData(100, 97.5)]
    public void LeavingTheRectanglePansAndSwallowsTheMenu(double x, double y)
    {
        var pan = Gesture();
        pan.Arm(100, 100);
        Assert.True(pan.Move(x, y));
        Assert.True(pan.IsPanning);
        Assert.True(pan.Move(100, 100)); // back inside: still panning
        pan.Release();
        Assert.False(pan.IsArmed);
        Assert.False(pan.TakeMenu());
    }

    [Fact]
    public void SwallowsOnlyOneMenu()
    {
        var pan = Gesture();
        pan.Arm(0, 0);
        pan.Move(50, 0);
        pan.Release();
        Assert.False(pan.TakeMenu());
        Assert.True(pan.TakeMenu());
    }

    [Fact]
    public void NextPressForgetsAnUnconsumedSwallow()
    {
        // WinUI may raise no RightTapped after a long drag; the next right click must still open its menu.
        var pan = Gesture();
        pan.Arm(0, 0);
        pan.Move(50, 0);
        pan.Release();
        pan.Arm(10, 10);
        pan.Release();
        Assert.True(pan.TakeMenu());
    }

    [Fact]
    public void LostCaptureMidPanStillSwallows()
    {
        var pan = Gesture();
        pan.Arm(0, 0);
        pan.Move(50, 0);
        pan.Cancel();
        Assert.False(pan.IsArmed);
        Assert.False(pan.TakeMenu());
    }

    [Fact]
    public void CaptureLostAfterReleaseKeepsTheSwallow()
    {
        var pan = Gesture();
        pan.Arm(0, 0);
        pan.Move(50, 0);
        pan.Release();
        pan.Cancel();
        Assert.False(pan.TakeMenu());
    }

    [Fact]
    public void MoveWithoutArmDoesNothing()
    {
        var pan = Gesture();
        Assert.False(pan.Move(500, 500));
        Assert.True(pan.TakeMenu());
    }

    [Theory]
    [InlineData(4, 4, 96u, 4, 4)]
    [InlineData(4, 4, 144u, 2.6667, 2.6667)]
    [InlineData(6, 8, 192u, 3, 4)]
    public void DragRectangleIsConvertedToDip(int cx, int cy, uint dpi, double width, double height)
    {
        var (w, h) = RightDragPan.DragRectDip(cx, cy, dpi);
        Assert.Equal(width, w, 3);
        Assert.Equal(height, h, 3);
    }

    [Fact]
    public void HighDpiShrinksTheDipThreshold()
    {
        // 6 px at 192 DPI = 3 DIP wide, so 1.6 DIP to the side is already a pan.
        var (w, h) = RightDragPan.DragRectDip(6, 6, 192);
        var pan = new RightDragPan(w, h);
        pan.Arm(0, 0);
        Assert.True(pan.Move(1.6, 0));
    }

    [Fact]
    public void ZeroDpiFallsBackTo96()
    {
        Assert.Equal((4d, 4d), RightDragPan.DragRectDip(4, 4, 0));
    }
}
