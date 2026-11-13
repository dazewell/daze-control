using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T24.8 (D-140): the validator mirrors the provider's address, answer range and wait checks.</summary>
public class NetworkReachValidationTests
{
    private static ValueList<Problem> Validate(string with)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: p, step: process.run, with: { path: 'a.exe' } }\n      - { id: r, step: network.reach, after: [p], with: { " + with + " } }\n");
        Assert.Empty(load.Problems);
        return ConfigValidator.Validate(load.Config!);
    }

    [Theory]
    [InlineData("nas:445")]
    [InlineData("  nas:445 ")]
    [InlineData("nas")]
    [InlineData("[::1]:80")]
    [InlineData("::1")]
    [InlineData("https://example.com/x?y=1")]
    [InlineData("{p.output}")]
    [InlineData("{p.output}:445")]
    public void ValidOrUnknownUntilTheRunIsAccepted(string address) => Assert.Empty(Validate($"address: '{address}'"));

    [Theory]
    [InlineData("nas:99999")]
    [InlineData("nas:0")]
    [InlineData("ftp://example.com")]
    [InlineData("http://user:pw@example.com")]
    [InlineData("a b")]
    [InlineData("{ not a value }")]
    [InlineData("nas:445 {")]
    [InlineData("1.2.3")]
    public void AWrittenAddressItWouldAlwaysRefuseIsAnError(string address)
    {
        var problem = Assert.Single(Validate($"address: '{address}'"));
        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal("flows.f.nodes.r.with.address", problem.Path);
        Assert.DoesNotContain("pw", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("200", true)]
    [InlineData("200-299, 401", true)]
    [InlineData("", true)]
    [InlineData("99", false)]
    [InlineData("300-200", false)]
    [InlineData("2xx", false)]
    [InlineData("200-", false)]
    public void TheAnswerRangeIsChecked(string status, bool ok) =>
        Assert.Equal(ok, Validate($"address: 'https://example.com', status: '{status}'").Count == 0);

    [Theory]
    [InlineData("5s", true)]
    [InlineData("1s", true)]
    [InlineData("20s", true)]
    [InlineData("500ms", false)]
    [InlineData("30s", false)]
    public void TheWaitIsOneToTwentySeconds(string timeout, bool ok) =>
        Assert.Equal(ok, Validate($"address: 'nas:445', timeout: '{timeout}'").Count == 0);

    [Fact]
    public void TheAddressIsRequired() =>
        Assert.NotEmpty(ConfigValidator.Validate(ConfigYaml.Load("flows:\n  f:\n    nodes:\n      - { id: r, step: network.reach }\n").Config!));
}
