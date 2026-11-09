using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

public class TriggerTests
{
    private const string Head = "version: 1\nflows:\n  lights:\n    nodes: []\n    triggers:\n";

    private static ConfigLoad Load(string triggers) => ConfigYaml.Load(Head + triggers);

    [Fact]
    public void SsidOnEngineStartIsAProblemNotSilentlyDropped()
    {
        var load = Load("    triggers:\n      - { on: engine.start, ssid: Home }\n");

        Assert.Contains(load.Problems, p => p.Message.Contains("ssid", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryKindRoundTrips()
    {
        var load = Load(string.Join('\n',
            "      - { at: \"22:30\", days: weekdays }",
            "      - { at: \"07:15\" }",
            "      - { at: \"09:00\", days: [mon, wed, sat], ifMissed: skip, grace: 5m }",
            "      - { every: 2h, between: [\"09:00\", \"18:00\"] }",
            "      - { every: 30m }",
            "      - { once: \"2026-12-01 09:30\", ifMissed: run, grace: 1h }",
            "      - { on: engine.start }",
            "      - { on: session.unlock, only: onBattery }",
            "      - { on: network.disconnected }",
            ""));
        Assert.Empty(load.Problems);
        var flow = load.Config!.Flows[0];
        Assert.Equal(9, flow.Triggers.Count);
        Assert.Equal(new AtTrigger(new(22, 30), Days.Weekdays), flow.Triggers[0]);
        Assert.Equal(new AtTrigger(new(7, 15), Days.Daily), flow.Triggers[1]);
        Assert.Equal(new AtTrigger(new(9, 0), Days.Mon | Days.Wed | Days.Sat) { IfMissed = IfMissed.Skip, Grace = TimeSpan.FromMinutes(5) }, flow.Triggers[2]);
        Assert.Equal(new EveryTrigger(TimeSpan.FromHours(2), new(9, 0), new(18, 0)), flow.Triggers[3]);
        Assert.IsType<EngineStartTrigger>(flow.Triggers[6]);
        Assert.Equal(new EventTrigger("session.unlock") { Only = "onBattery" }, flow.Triggers[7]);

        var saved = ConfigYaml.Save(load.Config);
        var again = ConfigYaml.Load(saved);
        Assert.Empty(again.Problems);
        Assert.Equal(load.Config, again.Config);
        Assert.Equal(saved, ConfigYaml.Save(again.Config!));
        Assert.DoesNotContain(ConfigValidator.Validate(load.Config), p => p.Path.Contains("triggers", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultsAreOmittedOnSave()
    {
        var saved = ConfigYaml.Save(Load("      - { at: \"07:15\", ifMissed: run }\n").Config!);
        Assert.Contains("07:15", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("days", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("ifMissed", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("grace", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("only", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowsWithoutTriggersWriteNone()
    {
        Assert.DoesNotContain("triggers", ConfigYaml.Save(ConfigYaml.Load("version: 1\nflows:\n  a:\n    nodes: []\n").Config!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ at: \"25:00\" }")]
    [InlineData("{ at: \"22:30\", days: someday }")]
    [InlineData("{ at: \"22:30\", days: [] }")]
    [InlineData("{ at: \"22:30\", ifMissed: maybe }")]
    [InlineData("{ at: \"22:30\", grace: soon }")]
    [InlineData("{ every: 10s }")]
    [InlineData("{ every: 25h }")]
    [InlineData("{ at: \"22:30\", only: onBattery }")]
    [InlineData("{ every: 2h, only: onAC }")]
    [InlineData("{ every: 2h, between: [\"09:00\"] }")]
    [InlineData("{ every: 2h, between: [\"09:00\", \"09:00\"] }")]
    [InlineData("{ between: [\"09:00\", \"18:00\"] }")]
    [InlineData("{ days: weekdays }")]
    [InlineData("{ once: tomorrow }")]
    [InlineData("{ on: session.exploded }")]
    [InlineData("{ on: session.lock, grace: 5m }")]
    [InlineData("{ at: \"22:30\", on: session.lock }")]
    [InlineData("{ }")]
    public void BadTriggerIsDroppedWithAWarningAndTheFlowStays(string bad)
    {
        var load = Load("      - " + bad + "\n      - { on: session.lock }\n");
        var problem = Assert.Single(load.Problems);
        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Equal("flows.lights.triggers[0]", problem.Path);
        Assert.Equal("lights", problem.FlowId);
        Assert.Empty(ConfigValidator.FlowsWithErrors(load.Problems.Concat(ConfigValidator.Validate(load.Config!, load.Problems))));
        Assert.Equal<Trigger[]>([new EventTrigger("session.lock")], [.. load.Config!.Flows[0].Triggers]);
    }

    [Fact]
    public void EditedModelProblemsAreWarningsToo()
    {
        var flow = new Flow("f", "f", [])
        {
            Triggers = [new EventTrigger("nope"), new EveryTrigger(TimeSpan.FromSeconds(5)), new EveryTrigger(TimeSpan.FromHours(25)), new AtTrigger(new(2, 0), Days.Daily) { Only = "onAC" }, new AtTrigger(new(1, 0), Days.None),
                new EventTrigger("session.lock"), new EventTrigger("session.lock")],
        };
        var problems = ConfigValidator.Validate(new Configuration { Flows = [flow] }).Where(p => p.Path.Contains("triggers", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, problems.Count);
        Assert.All(problems, p => Assert.Equal(Severity.Warning, p.Severity));
    }

    [Theory]
    [InlineData("daily", Days.Daily)]
    [InlineData("Weekdays", Days.Weekdays)]
    [InlineData("sun", Days.Sun)]
    [InlineData("friday", Days.Fri)]
    public void DaysParse(string text, Days expected)
    {
        Assert.True(TriggerText.TryParseDays(text, out var days));
        Assert.Equal(expected, days);
    }

    [Fact]
    public void EventNamesAreTheV1List()
    {
        Assert.Equal(13, TriggerText.Events.Count);
        Assert.All(TriggerText.Events, e => Assert.True(TriggerText.IsEvent(e)));
        Assert.False(TriggerText.IsEvent("window.opened"));
    }

    [Fact]
    public void OnlyMustBeAKnownCondition()
    {
        var flow = new Flow("f", "f", [])
        {
            Triggers = [new EventTrigger("session.lock") { Only = "onBattery" }, new EventTrigger("session.unlock") { Only = "onFire" }],
        };
        var problem = Assert.Single(ConfigValidator.Validate(new Configuration { Flows = [flow] }), p => p.Path.Contains("triggers", StringComparison.Ordinal));
        Assert.Equal(Severity.Warning, problem.Severity);
        Assert.Contains("onFire", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EventsReadAsWordsInHistory()
    {
        Assert.Equal("Event · PC unlocked", TriggerText.Describe(new EventTrigger("session.unlock")));
        Assert.All(TriggerText.Events, e => Assert.NotEqual(e, TriggerText.EventLabel(e)));
    }
}
