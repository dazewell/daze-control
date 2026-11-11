using DazeControl.Core.Catalog;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>D-118 checks in config.yaml: they load, save and validate like any step; Check time's window rules.</summary>
public class StateCheckValidationTests
{
    private static string Flow(string step, string with) => $$"""
        flows:
          f:
            nodes:
              - { id: c, step: {{step}}, with: { {{with}} } }

        """;

    private static string[] Errors(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        return [.. load.Problems.Concat(ConfigValidator.Validate(load.Config!)).Where(p => p.Severity == Severity.Error).Select(p => p.Path + ": " + p.Message)];
    }

    [Theory]
    [InlineData("network.wifi", "network: Home")]
    [InlineData("network.cable", "is: notConnected")]
    [InlineData("network.internet", "is: offline")]
    [InlineData("network.metered", "is: metered")]
    [InlineData("network.vpn", "name: WireGuard")]
    [InlineData("power.source", "is: onBattery")]
    [InlineData("power.battery", "percent: 30, is: below")]
    [InlineData("pc.idle", "for: 10m")]
    [InlineData("pc.locked", "is: unlocked")]
    [InlineData("display.count", "count: 2")]
    [InlineData("file.space", "drive: 'D:', gb: 50")]
    [InlineData("time.check", "days: weekdays, from: '22:00', to: '06:00'")]
    public void EveryCheckLoadsValidatesAndRoundTrips(string step, string with)
    {
        var yaml = Flow(step, with);
        Assert.Empty(Errors(yaml));
        var config = ConfigYaml.Load(yaml).Config!;
        Assert.Equal(config.Flows[0], ConfigYaml.Load(ConfigYaml.Save(config)).Config!.Flows[0]);
        Assert.Contains(step, ProviderCatalog.Checks);
    }

    [Theory]
    [InlineData("from: '7am', to: '09:00'", "isn't a time of day")]
    [InlineData("from: '07:00'", "both From and To")]
    [InlineData("to: '07:00'", "both From and To")]
    [InlineData("from: '07:00', to: '7:00'", "must differ")]
    [InlineData("days: sometimes", "isn't one of")]
    public void TimeWindowRules(string with, string message) =>
        Assert.Contains(Errors(Flow("time.check", with)), e => e.Contains(message, StringComparison.Ordinal));

    [Theory]
    [InlineData("power.battery", "percent: 0")]
    [InlineData("pc.idle", "for: soon")]
    [InlineData("display.count", "count: 0")]
    public void NumbersAndDurationsAreChecked(string step, string with) => Assert.NotEmpty(Errors(Flow(step, with)));
}
