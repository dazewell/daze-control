using System.Text.RegularExpressions;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Tests;

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
            "notify", "wait", "flag.set", "flag.check", "prompt.ask", "prompt.pick", "menu.show", "flow.run", "flow.stop", "text.make",
            "script.powershell", "process.run", "audio.check", "bluetooth.check", "process.check", "file.check", "file.space",
            "network.wifi", "network.cable", "network.internet", "network.metered", "network.vpn", "power.source", "power.battery",
            "pc.idle", "pc.locked", "time.check", "value.check",
            "text.extract", "process.open",
        ];

        foreach (var flow in config.Flows)
        {
            var steps = flow.Nodes.OfType<StepNode>().Select(n => n.Step).Distinct();
            if (flow.Name.StartsWith("LIVE-RISK ", StringComparison.Ordinal))
                continue;
            Assert.All(steps, s => Assert.Contains(s, safe));
            // flow.run and menu.show can start another flow: they must never reach a LIVE-RISK one.
            var started = flow.Nodes.OfType<StepNode>().SelectMany(n => n.With)
                .SelectMany(w => Texts(w.Value));
            var risky = config.Flows.Where(f => f.Name.StartsWith("LIVE-RISK ", StringComparison.Ordinal)).Select(f => f.Id).ToHashSet();
            Assert.DoesNotContain(started, risky.Contains);
            if (flow.Triggers.Count > 0)
                Assert.False(flow.Enabled, $"{flow.Name} has triggers and must start off");
        }
        Assert.Equal(4, config.Flows.Count(f => f.Name.StartsWith("LIVE-RISK ", StringComparison.Ordinal)));
    }

    /// <summary>Flow 39 may only open a local folder: never a web address or another scheme.</summary>
    [Fact]
    public void OpenStepsTargetOnlyLocalFolders()
    {
        var config = Kit(out _);
        var targets = config.Flows.SelectMany(f => f.Nodes.OfType<StepNode>())
            .Where(n => n.Step == "process.open").SelectMany(n => n.With).SelectMany(w => Texts(w.Value)).ToList();

        Assert.All(targets, t => Assert.StartsWith("%", t));
    }

    /// <summary>The walkthrough flows 30-40 (features since v0.54.0) must stay in the kit.</summary>
    [Fact]
    public void KitHasTheSinceV054Flows()
    {
        var config = Kit(out _);

        for (var n = 30; n <= 40; n++)
            Assert.Contains(config.Flows, f => f.Name.StartsWith($"{n} ", StringComparison.Ordinal));
        Assert.Contains(config.Flows, f => f.OnlyIf.Count > 0);
        Assert.Contains(config.Flows, f => f.Nodes.OfType<StepNode>().Any(s => !s.Enabled));
        Assert.Contains(config.Flows, f => f.Triggers.OfType<EventTrigger>().Any(t => t.Name == "file.created"));
        Assert.Contains(config.Flows, f => f.Triggers.OfType<EventTrigger>().Any(t => t.Name == "flow.finished"));
    }

    [Fact]
    public void HotkeysAreFKeyChordsOnly()
    {
        var config = Kit(out _);

        Assert.All(config.Flows.SelectMany(f => f.Hotkeys), h => Assert.Matches(@"^Ctrl\+Alt\+F\d+$", h));
    }

    private static IEnumerable<string> Texts(ParamValue value) => value switch
    {
        ScalarParam sp => [sp.Text],
        ListParam lp => lp.Items.SelectMany(Texts),
        _ => [],
    };

    [GeneratedRegex(@"^(\d+[a-z]? |LIVE-RISK \d+ )\S")]
    private static partial Regex NameShape();
}
