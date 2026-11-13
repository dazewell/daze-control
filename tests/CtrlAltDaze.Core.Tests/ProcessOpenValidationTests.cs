using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>T24.2 (D-134): the Open file or address step in the validator.</summary>
public class ProcessOpenValidationTests
{
    private static Core.Model.ValueList<Problem> Validate(string target)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: o, step: process.open, with: { target: '" + target + "' } }\n");
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("steam://rungameid/730")]
    [InlineData("ms-settings:display")]
    [InlineData(@"C:\Users\me\Documents")]
    public void OrdinaryTargetsAreValid(string target) => Assert.Empty(Validate(target));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript:{@time}")]
    [InlineData("https:")]
    [InlineData("ms-settings:")]
    [InlineData("ms-msdt:/id x")]
    [InlineData("\"C:\a.exe\" -x")]
    public void TargetsItNeverOpensAreErrors(string target)
    {
        var problem = Assert.Single(Validate(target));

        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal("flows.f.nodes.o.with.target", problem.Path);
    }

    [Fact]
    public void TheTargetIsRequired() =>
        Assert.NotEmpty(ConfigValidator.Validate(ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: o, step: process.open }\n").Config!));
}
