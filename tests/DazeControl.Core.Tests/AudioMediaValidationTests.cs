using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T24.5 (D-138): the Control media step in the validator (the action is a plain choice, so a wrong word is an error).</summary>
public class AudioMediaValidationTests
{
    private static Core.Model.ValueList<Problem> Validate(string with)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: m, step: audio.media" + with + " }\n");
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Theory]
    [InlineData("")]
    [InlineData(", with: { action: playPause }")]
    [InlineData(", with: { action: next }")]
    [InlineData(", with: { action: previous }")]
    public void KnownActionsAreValid(string with) => Assert.Empty(Validate(with));

    [Theory]
    [InlineData("rewind")]
    [InlineData("Play")]
    [InlineData("{@time}")]
    public void AnythingElseIsAnError(string action)
    {
        var problem = Assert.Single(Validate(", with: { action: '" + action + "' }"));

        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal("flows.f.nodes.m.with.action", problem.Path);
    }
}
