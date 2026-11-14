using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;

namespace CtrlAltDaze.Core.Tests;

public class ModelTests
{
    private static Flow TechnicsFlow() => new("connect-technics", "Connect Technics",
    [
        new StepNode("bt", "bluetooth.connect") { With = Params(("device", "technics")), Timeout = TimeSpan.FromSeconds(20) },
        new StepNode("out", "audio.setDefault") { With = Params(("device", "technics")), After = [new Edge("bt")] },
    ])
    { Hotkeys = ["Alt+Z"] };

    private static ValueDictionary<ParamValue> Params(params (string Key, ParamValue Value)[] entries) =>
        new(entries.Select(e => KeyValuePair.Create(e.Key, e.Value)));

    [Fact]
    public void FlowsCompareByContent()
    {
        Assert.Equal(TechnicsFlow(), TechnicsFlow());
        Assert.Equal(TechnicsFlow().GetHashCode(), TechnicsFlow().GetHashCode());
        Assert.NotEqual(TechnicsFlow(), TechnicsFlow() with { Hotkeys = ["Alt+X"] });
    }

    [Fact]
    public void NestedParamsCompareByContentAndMapsIgnoreKeyOrder()
    {
        var a = new MapParam(Params(("x", "1"), ("args", new ListParam(["-a", "-b"]))));
        var b = new MapParam(Params(("args", new ListParam(["-a", "-b"])), ("x", "1")));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new MapParam(Params(("x", "1"), ("args", new ListParam(["-b", "-a"])))));
    }

    [Fact]
    public void DefaultValueListIsEmpty()
    {
        Assert.Empty(default(ValueList<int>));
        Assert.Equal(default, (ValueList<int>)[]);
    }

    [Fact]
    public void ValueDictionaryKeepsFirstPositionForDuplicateKeys()
    {
        var map = Params(("a", "1"), ("b", "2"), ("a", "3"));
        Assert.Equal(["a", "b"], map.Keys);
        Assert.Equal(new ScalarParam("3"), map["a"]);
    }

    [Fact]
    public void FindNodeByIdAndLibraryLookup()
    {
        Assert.IsType<StepNode>(TechnicsFlow().FindNode("out"));
        Assert.Null(TechnicsFlow().FindNode("nope"));
        var library = new Library { Bluetooth = [new BluetoothDevice("technics", "B8:20:8E:ED:48:FC")] };
        Assert.True(library.Contains(LibraryKind.Bluetooth, "technics"));
        Assert.False(library.Contains(LibraryKind.Audio, "technics"));
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("20s", 20_000)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("1m30s", 90_000)]
    [InlineData(" 1S500MS ", 1_500)]
    [InlineData("0s", 0)]
    public void ParsesDurations(string text, int milliseconds)
    {
        Assert.True(Duration.TryParse(text, out var duration));
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20")]
    [InlineData("s")]
    [InlineData("1d")]
    [InlineData("30s1m")]
    [InlineData("1s1s")]
    [InlineData("-5s")]
    [InlineData("1.5s")]
    [InlineData("99999999999999999999h")]
    public void RejectsBadDurations(string? text) => Assert.False(Duration.TryParse(text, out _));

    [Theory]
    [InlineData(90_000, "1m30s")]
    [InlineData(20_000, "20s")]
    [InlineData(1_500, "1s500ms")]
    [InlineData(0, "0s")]
    public void FormatsDurationsLargestUnitFirst(int milliseconds, string text) =>
        Assert.Equal(text, Duration.Format(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void FormatNullIsNull() => Assert.Null(Duration.Format(null));

    [Theory]
    [InlineData(new[] { NodeOutcome.Succeeded, NodeOutcome.Succeeded }, FlowOutcome.Succeeded)]
    [InlineData(new NodeOutcome[0], FlowOutcome.Succeeded)]
    [InlineData(new[] { NodeOutcome.Succeeded, NodeOutcome.FailedIgnored }, FlowOutcome.PartiallySucceeded)]
    [InlineData(new[] { NodeOutcome.Failed, NodeOutcome.Skipped, NodeOutcome.Succeeded }, FlowOutcome.PartiallySucceeded)]
    [InlineData(new[] { NodeOutcome.Failed, NodeOutcome.Skipped }, FlowOutcome.Failed)]
    [InlineData(new[] { NodeOutcome.FailedIgnored }, FlowOutcome.Failed)]
    public void SummarizesFinishedRuns(NodeOutcome[] nodes, FlowOutcome expected) =>
        Assert.Equal(expected, Outcomes.Summarize(nodes));

    [Fact]
    public void CancelBeatsStopBeatsNodeOutcomes()
    {
        NodeOutcome[] nodes = [NodeOutcome.Succeeded, NodeOutcome.Cancelled];
        Assert.Equal(FlowOutcome.Cancelled, Outcomes.Summarize(nodes, cancelled: true, stopped: true));
        Assert.Equal(FlowOutcome.Failed, Outcomes.Summarize(nodes, stopped: true));
    }
}
