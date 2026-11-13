using DazeControl.Providers.Basic;

namespace DazeControl.Providers.Tests;

/// <summary>D-132: Windows keeps one balloon per tray icon, so back-to-back <c>notify</c> steps must be spaced, not dropped.</summary>
public sealed class NoticeSpacerTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly List<string> _shown = [];

    public void Dispose() => _time.Dispose();

    private NoticeSpacer Queue() => new((_, text, _) => _shown.Add(text), _time);

    [Fact]
    public void TheFirstShowsAtOnceAndTheNextWaitsItsGap()
    {
        using var queue = Queue();

        queue.Show("6 Make text", "One", false);
        queue.Show("6 Make text", "Two", false);
        Assert.Equal(["One"], _shown);

        _time.Advance(NoticeSpacer.Gap - TimeSpan.FromMilliseconds(1));
        Assert.Equal(["One"], _shown);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["One", "Two"], _shown);
    }

    [Fact]
    public void ThreeInARowAreAllShownInOrder()
    {
        using var queue = Queue();

        foreach (var text in new[] { "a", "b", "c" })
            queue.Show("f", text, true);
        _time.Advance(NoticeSpacer.Gap);
        _time.Advance(NoticeSpacer.Gap);

        Assert.Equal(["a", "b", "c"], _shown);
    }

    [Fact]
    public void AfterTheQueueDrainsTheNextShowsAtOnce()
    {
        using var queue = Queue();
        queue.Show("f", "a", true);
        _time.Advance(NoticeSpacer.Gap);

        queue.Show("f", "b", true);

        Assert.Equal(["a", "b"], _shown);
    }

    [Fact]
    public void AFloodKeepsTheNewestTwentyWaiting()
    {
        using var queue = Queue();

        for (var i = 0; i < 30; i++)
            queue.Show("f", i.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
        for (var i = 0; i < 25; i++)
            _time.Advance(NoticeSpacer.Gap);

        Assert.Equal(21, _shown.Count);
        Assert.Equal(["0", "10", "29"], new[] { _shown[0], _shown[1], _shown[^1] });
    }

    [Fact]
    public void AfterDisposeNothingMoreShows()
    {
        var queue = Queue();
        queue.Show("f", "a", true);
        queue.Show("f", "b", true);
        queue.Dispose();

        _time.Advance(NoticeSpacer.Gap);
        queue.Show("f", "c", true);

        Assert.Equal(["a"], _shown);
    }
}

