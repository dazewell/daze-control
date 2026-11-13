using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

/// <summary>T24.4: only a change raises; the value Windows reports on registration is the baseline.</summary>
public sealed class ScreenAwayDetectorTests
{
    private readonly ScreenAwayDetector _detector = new();

    [Fact]
    public void TheFirstReadingsAreBaselines()
    {
        Assert.Null(_detector.Display(0));
        Assert.Null(_detector.Presence(1));
    }

    [Fact]
    public void OnlyAChangeRaisesDisplayEvents()
    {
        Assert.Null(_detector.Display(1));
        Assert.Null(_detector.Display(1));
        Assert.Equal("display.off", _detector.Display(0));
        Assert.Null(_detector.Display(0));
        Assert.Equal("display.on", _detector.Display(1));
    }

    [Fact]
    public void DimmingIsNotOffOrOn()
    {
        _detector.Display(1);
        Assert.Null(_detector.Display(2));
        Assert.Equal("display.off", _detector.Display(0));
        Assert.Equal("display.on", _detector.Display(2));
        Assert.Null(_detector.Display(1));
    }

    [Fact]
    public void OnlyAChangeRaisesPresenceEvents()
    {
        Assert.Null(_detector.Presence(0));
        Assert.Equal("session.away", _detector.Presence(1));
        Assert.Null(_detector.Presence(2)); // not present -> inactive is still away
        Assert.Equal("session.back", _detector.Presence(0));
        Assert.Null(_detector.Presence(0));
    }

    [Fact]
    public void ResetMakesTheNextReadingABaseline()
    {
        _detector.Display(1);
        _detector.Presence(0);
        _detector.Reset();
        Assert.Null(_detector.Display(0));
        Assert.Null(_detector.Presence(1));
        Assert.Equal("display.on", _detector.Display(1));
    }
}
