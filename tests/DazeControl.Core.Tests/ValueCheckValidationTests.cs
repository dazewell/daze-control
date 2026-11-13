using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T24.3 (D-135): the Check value step in the validator mirrors what the provider always refuses.</summary>
public class ValueCheckValidationTests
{
    private static Core.Model.ValueList<Problem> Validate(string with)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: q, step: prompt.ask, with: { question: Q } }\n      - { id: c, step: value.check, with: { " + with + " }, after: [q] }\n");
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Theory]
    [InlineData("value: '{q.answer}', is: is, to: Yes")]
    [InlineData("value: '{q.answer}', is: contains, to: Y")]
    [InlineData("value: '{q.answer}', is: contains, to: '{q.answer}'")]
    [InlineData("value: '{q.answer}', is: isEmpty")]
    [InlineData("value: '5', is: atLeast, to: '2.5'")]
    [InlineData("value: '{q.answer|5}', is: below, to: '3'")]
    [InlineData("value: ' 5 ', is: atLeast, to: ' 3 '")]
    [InlineData("is: isNotEmpty")]
    public void OrdinaryChecksAreValid(string with) => Assert.Empty(Validate(with));

    [Theory]
    [InlineData("value: x, is: contains", "with.to")]
    [InlineData("value: x, is: startsWith, to: ''", "with.to")]
    [InlineData("value: x, is: endsWith, to: '  '", "with.to")]
    [InlineData("value: x, is: doesNotContain, to: ''", "with.to")]
    [InlineData("value: abc, is: atLeast, to: '3'", "with.value")]
    [InlineData("value: '3', is: below, to: three", "with.to")]
    [InlineData("value: NaN, is: atLeast, to: '3'", "with.value")]
    [InlineData("is: atLeast, to: '3'", "with.value")]
    public void ComparisonsItAlwaysRefusesAreErrors(string with, string setting)
    {
        var problem = Assert.Single(Validate(with), p => p.Severity == Severity.Error);
        Assert.EndsWith(setting, problem.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextWithAReferenceIsLeftToTheRun() =>
        Assert.Empty(Validate("value: '{q.answer}', is: atLeast, to: '{q.answer|3}'"));

    [Fact]
    public void AnUnknownComparisonIsAnError() =>
        Assert.Contains(Validate("value: x, is: sideways, to: y"), p => p.Severity == Severity.Error);
}
