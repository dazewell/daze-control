using System.Text.RegularExpressions;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>samples/acceptance/config.yaml (docs/ACCEPTANCE.md): the kit the user runs for the live checks.</summary>
public sealed partial class AcceptanceKitTests
{
    private static Configuration Kit(out IReadOnlyList<Problem> problems)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "acceptance", "config.yaml"));
        var load = ConfigYaml.Load(text);
        Assert.Empty(load.Problems);
        problems = [.. ConfigValidator.Validate(load.Config!)];
        return load.Config!;
    }

    [Fact]
    public void KitLoadsWithoutProblems()
    {
        Kit(out var problems);

        Assert.Empty(problems);
    }

    [Fact]
    public void EveryFlowNameStartsWithItsNumberOrLiveRisk()
    {
        var config = Kit(out _);

        Assert.All(config.Flows, f => Assert.Matches(NameShape(), f.Name));
        Assert.Equal(config.Flows.Count, config.Flows.Select(f => f.Name).Distinct().Count());
    }

    /// <summary>Only the LIVE-RISK flows may act on real devices; everything else checks, shows, waits, asks or runs text.</summary>
    [Fact]
    public void NothingRiskyRunsOutsideLiveRiskFlows()
    {
        var config = Kit(out _);
        HashSet<string> safe =
        [
            "notify", "wait", "flag.set", "flag.check", "prompt.ask", "prompt.pick", "menu.show", "flow.run", "text.make",
            "script.powershell", "process.run", "audio.check", "bluetooth.check", "process.check", "file.check", "file.space",
            "network.wifi", "network.cable", "network.internet", "network.metered", "network.vpn", "power.source", "power.battery",
            "pc.idle", "pc.locked", "time.check",
        ];

        foreach (var flow in config.Flows)
        {
            var steps = flow.Nodes.OfType<StepNode>().Select(n => n.Step).Distinct();
            if (flow.Name.StartsWith("LIVE-RISK ", StringComparison.Ordinal))
                continue;
            Assert.All(steps, s => Assert.Contains(s, safe));
            if (flow.Triggers.Count > 0)
                Assert.False(flow.Enabled, $"{flow.Name} has triggers and must start off");
        }
        Assert.Equal(2, config.Flows.Count(f => f.Name.StartsWith("LIVE-RISK ", StringComparison.Ordinal)));
    }

    [Fact]
    public void HotkeysAreFKeyChordsOnly()
    {
        var config = Kit(out _);

        Assert.All(config.Flows.SelectMany(f => f.Hotkeys), h => Assert.Matches(@"^Ctrl\+Alt\+F\d+$", h));
    }

    [GeneratedRegex(@"^(\d+[a-z]? |LIVE-RISK \d+ )\S")]
    private static partial Regex NameShape();
}
