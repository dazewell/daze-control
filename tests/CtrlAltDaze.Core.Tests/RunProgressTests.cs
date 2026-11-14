using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;

namespace CtrlAltDaze.Core.Tests;

public sealed class RunProgressTests
{
    // a → card(m1, m2) → b, with fix as a's fallback: four steps plus a card that doesn't count (F-012, D-127).
    private static readonly Flow Shape = new("f", "F",
    [
        new StepNode("a", "wait"),
        new GroupNode("card", ["m1", "m2"]) { After = [new Edge("a")] },
        new StepNode("m1", "wait"),
        new StepNode("m2", "wait"),
        new StepNode("fix", "wait") { After = [new Edge("a", EdgeWhen.Failure)] },
    ]);

    [Fact]
    public void TheTotalIsTheStepCountFromTheStartFallbacksIncludedCardsNot()
    {
        var progress = new RunProgress(Shape);

        Assert.Equal("0 of 4", progress.Text);
        Assert.True(progress.Counts("fix"));
        Assert.False(progress.Counts("card"));
    }

    [Fact]
    public void DoneOnlyGrowsAndTheTotalNeverMoves()
    {
        var progress = new RunProgress(Shape);
        var seen = new List<(int Done, int Total, double Fraction)>();
        foreach (var id in new[] { "a", "a", "card", "m2", "nested-step", "m1", "fix" })
        {
            progress.Finish(id);
            seen.Add((progress.Done, progress.Total, progress.Fraction()));
        }

        Assert.All(seen, s => Assert.Equal(4, s.Total));
        Assert.Equal([1, 1, 1, 2, 2, 3, 4], seen.Select(s => s.Done));
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second.Fraction >= p.First.Fraction));
        Assert.Equal(1.0, progress.Fraction(running: 0.5)); // never above 1
    }

    [Fact]
    public void AnEmptyFlowIsZeroOfZero()
    {
        var progress = new RunProgress(new Flow("e", "E", []));

        Assert.Equal(("0 of 0", 0.0), (progress.Text, progress.Fraction()));
    }
}
