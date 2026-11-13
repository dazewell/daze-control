using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

/// <summary>T24.7 (D-139): the Wake over network step in the validator mirrors the provider's MAC check.</summary>
public class NetworkWakeValidationTests
{
    private static Core.Model.ValueList<Problem> Validate(string mac)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: p, step: process.run, with: { path: 'a.exe' } }\n      - { id: w, step: network.wake, after: [p], with: { mac: '" + mac + "' } }\n");
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aabb.ccdd.eeff")]
    [InlineData("aabbccddeeff")]
    [InlineData("{p.output}")]
    [InlineData("aa:bb:cc:dd:ee:{p.output}")]
    public void ValidOrUnknownUntilTheRunIsAccepted(string mac) => Assert.Empty(Validate(mac));

    [Theory]
    [InlineData("aa:bb:cc")]
    [InlineData("aa:bb:cc:dd:ee:gg")]
    [InlineData("aa:bb-cc:dd-ee:ff")]
    [InlineData("hello")]
    [InlineData("{ not a value }")]
    [InlineData("aa:bb:cc:dd:ee:ff {")]
    public void AWrittenMacItWouldAlwaysRefuseIsAnError(string mac)
    {
        var problem = Assert.Single(Validate(mac));
        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal("flows.f.nodes.w.with.mac", problem.Path);
    }

    [Fact]
    public void TheMacIsRequired() =>
        Assert.NotEmpty(ConfigValidator.Validate(ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: w, step: network.wake }\n").Config!));
}
