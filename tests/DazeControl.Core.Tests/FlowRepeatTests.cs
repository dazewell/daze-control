using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T27.6 (D-150): "Repeat" on Run flow, in the validator and in the reader the runner uses.</summary>
public class FlowRepeatTests
{
    private static List<Problem> Validate(string with)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: c, step: flow.run, with: { flow: g" + with + " } }\n"
            + "  g:\n    nodes:\n      - { id: n, step: notify, with: { text: x } }\n");
        Assert.Empty(load.Problems);
        return [.. ConfigValidator.Validate(load.Config!).Where(p => p.Severity == Severity.Error)];
    }

    private static StepNode Step(string with) =>
        (StepNode)ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: c, step: flow.run, with: { flow: g" + with + " } }\n").Config!.Flows[0].Nodes[0];

    [Theory]
    [InlineData("")]
    [InlineData(", repeat: 1")]
    [InlineData(", repeat: 50, until: fails, pause: 10m")]
    [InlineData(", repeat: 1, wait: false")]
    public void RepeatsThatCanRunAreValid(string with) => Assert.Empty(Validate(with));

    [Theory]
    [InlineData(", repeat: 0", "with.repeat")]
    [InlineData(", repeat: 51", "with.repeat")]
    [InlineData(", repeat: 2.5", "with.repeat")]
    [InlineData(", until: never", "with.until")]
    [InlineData(", pause: soon", "with.pause")]
    [InlineData(", repeat: 3, pause: 11m", "with.pause")]
    [InlineData(", repeat: 3, wait: false", "with.repeat")]
    [InlineData(", repeat: 3, pause: -1s", "with.pause")]
    public void RepeatsThatCantRunAreErrors(string with, string where) =>
        Assert.Equal("flows.f.nodes.c." + where, Assert.Single(Validate(with)).Path);

    [Fact]
    public void TheReaderGivesThePlanOrASentence()
    {
        Assert.Equal(new FlowRepeat.Plan(1, true, TimeSpan.Zero), FlowRepeat.Read(Step(""), out _));
        Assert.Equal(new FlowRepeat.Plan(4, false, TimeSpan.FromSeconds(2)), FlowRepeat.Read(Step(", repeat: ' 4 ', until: fails, pause: 2s"), out _));
        Assert.Null(FlowRepeat.Read(Step(", repeat: 99"), out var problem));
        Assert.Equal("Repeat must be 1 to 50 times.", problem);
        Assert.Null(FlowRepeat.Read(Step(", repeat: secret7"), out problem));
        Assert.Equal("Repeat isn't a whole number.", problem); // never the text: it may hold a Sensitive value
        Assert.Null(FlowRepeat.Read(Step(", repeat: 2, pause: 1h"), out problem));
        Assert.Equal("Wait 0 to 10m between rounds.", problem);
    }
}
