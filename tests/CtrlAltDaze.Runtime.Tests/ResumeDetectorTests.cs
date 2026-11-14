using CtrlAltDaze.Runtime.Triggers;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>Modern Standby sends only display on/off; classic S3 sends a resume message. Both must raise once per wake-up.</summary>
public sealed class ResumeDetectorTests : IDisposable
{
    private readonly ManualTime _time = new();
    private int _count;
    private readonly ResumeDetector _detector;

    public ResumeDetectorTests() => _detector = new ResumeDetector(_time, () => _count++);

    public void Dispose() => _time.Dispose();

    [Fact]
    public void TheFirstDisplayReadingIsABaseline()
    {
        _detector.DisplayState(1);
        _detector.DisplayState(0);
        Assert.Equal(0, _count);
    }

    [Fact]
    public void DisplayOffThenOnRaises()
    {
        _detector.DisplayState(1);
        _detector.DisplayState(0);
        _detector.DisplayState(1);
        Assert.Equal(1, _count);
    }

    [Fact]
    public void ABaselineOfOffThenOnRaises()
    {
        _detector.DisplayState(0); // registered while the screen was off: still a wake when it comes back
        _detector.DisplayState(1);
        Assert.Equal(1, _count);
    }

    [Fact]
    public void RepeatedOnReadingsDoNotRaise()
    {
        _detector.DisplayState(1);
        _detector.DisplayState(1);
        _detector.DisplayState(2);
        _detector.DisplayState(1);
        Assert.Equal(0, _count);
    }

    [Fact]
    public void ClassicResumeRaises()
    {
        _detector.ResumeAutomatic();
        Assert.Equal(1, _count);
    }

    [Fact]
    public void ClassicResumeAndDisplayOnForOneWakeRaiseOnce()
    {
        _detector.DisplayState(1);
        _detector.DisplayState(0);
        _time.Advance(TimeSpan.FromSeconds(3));
        _detector.ResumeAutomatic();
        _time.Advance(TimeSpan.FromSeconds(1));
        _detector.DisplayState(1);
        Assert.Equal(1, _count);
    }

    [Fact]
    public void ASecondWakeLaterRaisesAgain()
    {
        _detector.DisplayState(1);
        _detector.DisplayState(0);
        _detector.DisplayState(1);
        _time.Advance(ResumeDetector.Window + TimeSpan.FromSeconds(1));
        _detector.DisplayState(0);
        _detector.DisplayState(1);
        Assert.Equal(2, _count);
    }

    [Fact]
    public void ResetMakesTheNextReadingABaseline()
    {
        _detector.DisplayState(0);
        _detector.Reset();
        _detector.DisplayState(1);
        Assert.Equal(0, _count);
    }
}
