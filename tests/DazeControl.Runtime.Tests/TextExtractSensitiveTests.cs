using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime.Tests;

/// <summary>T25.2 (D-144): a Get from text reading a value marked Sensitive during the run (a called flow's output) is Sensitive too.</summary>
public class TextExtractSensitiveTests
{
    private static StepNode Extract(string text, string find = "a") =>
        new("g", "text.extract") { With = new ValueDictionary<ParamValue>([KeyValuePair.Create("text", (ParamValue)text), KeyValuePair.Create("find", (ParamValue)find)]) };

    [Fact]
    public void ItsValuesFollowASensitiveMarkOfWhatItReads()
    {
        var values = new RunValues();
        values.MarkSensitive("call", "answer");

        FlowRunner.MarkExtractSensitive(values, Extract("{call.answer}"));

        Assert.True(values.IsMarkedSensitive("g", "text"));
        Assert.True(values.IsMarkedSensitive("g", "number"));
    }

    [Fact]
    public void ItsLookedForTextCountsToo()
    {
        var values = new RunValues();
        values.MarkSensitive("call", "answer");

        FlowRunner.MarkExtractSensitive(values, Extract("plain", "{call.answer}"));

        Assert.True(values.IsMarkedSensitive("g", "text"));
    }

    [Fact]
    public void ReadingNothingMarkedLeavesThemAlone()
    {
        var values = new RunValues();

        FlowRunner.MarkExtractSensitive(values, Extract("{call.answer}"));

        Assert.False(values.IsMarkedSensitive("g", "text"));
    }
}
