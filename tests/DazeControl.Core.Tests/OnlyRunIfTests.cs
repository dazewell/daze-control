using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T25.1 (D-148): a flow's "Only run if" list loads, saves and validates like steps, under <c>onlyIf</c>.</summary>
public class OnlyRunIfTests
{
    private const string Guarded = """
        flows:
          f:
            onlyIf:
            - step: network.wifi
              with:
                is: connected
                network: Home
            - id: late
              step: time.check
              with:
                days: weekdays
              timeout: 3s
            onlyIfByHand: true
            nodes:
            - id: a
              step: notify
              with:
                text: Hi
        """;

    [Fact]
    public void TheListLoadsWithIdsAndSavesBackTheSame()
    {
        var load = ConfigYaml.Load(Guarded);
        Assert.Empty(load.Problems);
        var flow = Assert.Single(load.Config!.Flows);
        Assert.Equal(["if1", "late"], flow.OnlyIf.Select(c => c.Id));
        Assert.Equal("network.wifi", flow.OnlyIf[0].Step);
        Assert.Equal(TimeSpan.FromSeconds(3), flow.OnlyIf[1].Timeout);
        Assert.True(flow.OnlyIfByHand);

        var saved = ConfigYaml.Save(load.Config);
        Assert.DoesNotContain("id: if1", saved, StringComparison.Ordinal); // the place-given id isn't written
        Assert.Contains("id: late", saved, StringComparison.Ordinal);
        Assert.Equal(saved, ConfigYaml.Save(ConfigYaml.Load(saved).Config!));
        Assert.Empty(string.Join(" | ", ConfigValidator.Validate(load.Config).Where(p => p.Severity == Severity.Error).Select(p => p.Path + ": " + p.Message)));
    }

    [Fact]
    public void AFlowWithoutTheListWritesNothingNew()
    {
        var saved = ConfigYaml.Save(ConfigYaml.Load("flows:\n  f:\n    nodes:\n    - id: a\n      step: notify\n      with:\n        text: Hi\n").Config!);
        Assert.DoesNotContain("onlyIf", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyChecksMayGuard()
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    onlyIf:\n    - step: notify\n      with: { text: Hi }\n    nodes: []\n");
        var problem = Assert.Single(ConfigValidator.Validate(load.Config!), p => p.Severity == Severity.Error);
        Assert.Equal("flows.f.onlyIf.if1.step", problem.Path);
        Assert.Equal("f", problem.FlowId);
    }

    [Fact]
    public void ACheckIsValidatedLikeAStepUnderItsOwnPath()
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    onlyIf:\n    - step: network.wifi\n      with: { is: sideways }\n    - step: power.source\n      with: { nope: x }\n    nodes: []\n");
        var errors = ConfigValidator.Validate(load.Config!).Where(p => p.Severity == Severity.Error).Select(p => p.Path).ToList();
        Assert.Contains("flows.f.onlyIf.if1.with.is", errors);
        Assert.Contains("flows.f.onlyIf.if2.with.nope", errors);
        Assert.DoesNotContain(errors, p => p.Contains(".nodes.", StringComparison.Ordinal));
    }

    [Fact]
    public void EdgesAndCardsAreRefusedOnLoad()
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    onlyIf:\n    - id: c\n      step: power.source\n      after: [x]\n    nodes: []\n");
        Assert.Contains(load.Problems, p => p.Path == "flows.f.onlyIf.c");
        Assert.Empty(load.Config!.Flows[0].OnlyIf);
    }
}
