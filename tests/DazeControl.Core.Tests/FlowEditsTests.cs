using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class FlowEditsTests
{
    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static ValueList<Edge> After(Flow flow, string id) => flow.FindNode(id)!.After;

    private static Edge E(string from) => new(from);

    [Fact]
    public void GroupMovesOuterEdgesToTheCard()
    {
        // a → b, a → c, b → d, c → d: grouping b and c makes a → [b c] → d.
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b", "c"));

        var (grouped, refusal) = FlowEdits.Group(flow, ["b", "c"]);

        Assert.Equal(ConnectRefusal.None, refusal);
        var card = Assert.IsType<GroupNode>(grouped.FindNode("together"));
        Assert.Equal(["b", "c"], card.Members);
        Assert.Equal([E("a")], card.After);
        Assert.Empty(After(grouped, "b"));
        Assert.Equal([E("together")], After(grouped, "d"));
        Assert.Equal(["a", "together", "b", "c", "d"], grouped.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void GroupRefusesStepsThatWaitForEachOther()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"));

        var (result, refusal) = FlowEdits.Group(flow, ["a", "b"]);

        Assert.Equal(ConnectRefusal.SameCard, refusal);
        Assert.Same(flow, result);
    }

    [Fact]
    public void GroupRefusesALoopThroughAnOutsideStep()
    {
        // a → x → c: a card [a c] would wait for x, which waits for the card.
        var flow = FlowOf(Step("a"), Step("x", "a"), Step("c", "x"));

        Assert.Equal(ConnectRefusal.Loop, FlowEdits.Group(flow, ["a", "c"]).Refusal);
    }

    [Fact]
    public void GroupWithACardMergesIntoIt()
    {
        var flow = FlowOf(new GroupNode("g", ["m1", "m2"]) { Name = "Mine" }, Step("m1"), Step("m2"), Step("s"));

        var (grouped, _) = FlowEdits.Group(flow, ["g", "s"]);

        var card = Assert.IsType<GroupNode>(grouped.FindNode("g"));
        Assert.Equal(["m1", "m2", "s"], card.Members);
        Assert.Equal("Mine", card.Name);
        Assert.Single(grouped.Nodes.OfType<GroupNode>());
    }

    [Fact]
    public void GroupNeedsTwoSteps()
    {
        var flow = FlowOf(Step("a"), Step("b"));

        Assert.Same(flow, FlowEdits.Group(flow, ["a"]).Flow);
    }

    [Fact]
    public void NewCardIdsDontCollide()
    {
        var flow = FlowOf(Step("together"), Step("a"), Step("b"));

        Assert.NotNull(FlowEdits.Group(flow, ["a", "b"]).Flow.FindNode("together-2"));
    }

    [Fact]
    public void UngroupRefusesWhileAFallbackWaitsForTheCardToFail()
    {
        // "If the card fails" = any step failed; per-step failure edges would need every step to fail.
        var flow = FlowOf(new GroupNode("g", ["m1", "m2"]), Step("m1"), Step("m2"),
            new StepNode("z", "wait") { After = [new Edge("g", EdgeWhen.Failure)] });

        var (same, refusal) = FlowEdits.Ungroup(flow, "g");

        Assert.Equal(ConnectRefusal.FailureFallback, refusal);
        Assert.Same(flow, same);
    }

    [Fact]
    public void TakeOutRefusesWhileAFallbackWaitsForTheCardToFail()
    {
        // m1 failing would no longer count as "the card failed".
        var flow = FlowOf(new GroupNode("g", ["m1", "m2"]), Step("m1"), Step("m2"),
            new StepNode("z", "wait") { After = [new Edge("g", EdgeWhen.Failure)] });

        var (same, refusal) = FlowEdits.TakeOut(flow, "m1");

        Assert.Equal(ConnectRefusal.FailureFallback, refusal);
        Assert.Same(flow, same);
    }

    [Fact]
    public void GroupRefusesWhenSomethingWaitsForAStepToFail()
    {
        // z ran only if a failed; in a card it would run if b failed too.
        var z = new StepNode("z", "wait") { After = [new Edge("a", EdgeWhen.Failure)] };

        Assert.Equal(ConnectRefusal.FailureFallback, FlowEdits.Group(FlowOf(Step("a"), Step("b"), z), ["a", "b"]).Refusal);
    }

    [Fact]
    public void GroupRefusesStepsThatHandleFailureDifferently()
    {
        var skip = new StepNode("b", "wait") { OnFailure = FailurePolicy.Skip };

        Assert.Equal(ConnectRefusal.FailurePolicies, FlowEdits.Group(FlowOf(Step("a"), skip), ["a", "b"]).Refusal);
    }

    [Fact]
    public void GroupGivesTheCardTheStepsSharedPolicyAndKeepsMayFail()
    {
        var flow = FlowOf(
            new StepNode("a", "wait") { OnFailure = FailurePolicy.Skip },
            new StepNode("b", "wait") { OnFailure = FailurePolicy.Skip },
            new StepNode("c", "wait") { OnFailure = FailurePolicy.Ignore });

        var (grouped, refusal) = FlowEdits.Group(flow, ["a", "b", "c"]);

        Assert.Equal(ConnectRefusal.None, refusal);
        Assert.Equal(FailurePolicy.Skip, grouped.FindNode("together")!.OnFailure);
    }

    [Fact]
    public void StepsLeavingACardKeepTheCardsPolicyUnlessTheyMayFail()
    {
        var flow = FlowOf(
            new GroupNode("g", ["m1", "m2"]) { OnFailure = FailurePolicy.Ignore },
            Step("m1"), new StepNode("m2", "wait") { OnFailure = FailurePolicy.Ignore }, Step("z", "g"));

        var (taken, refusal) = FlowEdits.TakeOut(flow, "m1");

        Assert.Equal(ConnectRefusal.None, refusal);
        Assert.Null(taken.FindNode("g"));
        Assert.Equal(FailurePolicy.Ignore, taken.FindNode("m1")!.OnFailure);
        Assert.Equal(FailurePolicy.Ignore, taken.FindNode("m2")!.OnFailure);
        Assert.Equal(FailurePolicy.Ignore, FlowEdits.Ungroup(flow, "g").Flow.FindNode("m1")!.OnFailure);
    }

    [Fact]
    public void GroupRefusesToMergeDifferentConditionsOnOneSource()
    {
        var a = Step("a");
        var ok = new StepNode("ok", "wait") { After = [E("a")] };
        var fallback = new StepNode("fallback", "wait") { After = [new Edge("a", EdgeWhen.Failure)] };

        var (same, refusal) = FlowEdits.Group(FlowOf(a, ok, fallback), ["ok", "fallback"]);

        Assert.Equal(ConnectRefusal.Conditions, refusal);
        Assert.Equal(3, same.Nodes.Count);
    }

    [Fact]
    public void GroupRefusesWhenADependentWaitsOnMembersUnderDifferentConditions()
    {
        var z = new StepNode("z", "wait") { After = [E("a"), new Edge("b", EdgeWhen.Failure)] };

        Assert.Equal(ConnectRefusal.Conditions, FlowEdits.Group(FlowOf(Step("a"), Step("b"), z), ["a", "b"]).Refusal);
    }

    [Fact]
    public void UngroupKeepsEdgesOnEveryMember()
    {
        var flow = FlowOf(Step("a"), new GroupNode("g", ["m1", "m2"]) { After = [E("a")] }, Step("m1"), Step("m2"), Step("z", "g"));

        var (ungrouped, refusal) = FlowEdits.Ungroup(flow, "g");

        Assert.Equal(ConnectRefusal.None, refusal);
        Assert.Null(ungrouped.FindNode("g"));
        Assert.Equal([E("a")], After(ungrouped, "m1"));
        Assert.Equal([E("a")], After(ungrouped, "m2"));
        Assert.Equal([E("m1"), E("m2")], After(ungrouped, "z"));
        Assert.Equal(FlowLayout.Layers(ungrouped)["m1"], FlowLayout.Layers(ungrouped)["m2"]);
    }

    [Fact]
    public void TakingOutTheSecondToLastMemberDissolvesTheCard()
    {
        var flow = FlowOf(Step("a"), new GroupNode("g", ["m1", "m2"]) { After = [E("a")] }, Step("m1"), Step("m2"), Step("z", "g"));

        var taken = FlowEdits.TakeOut(flow, "m1").Flow;

        Assert.Null(taken.FindNode("g"));
        Assert.Equal([E("a")], After(taken, "m1"));
        Assert.Equal([E("m2")], After(taken, "z"));
    }

    [Fact]
    public void TakingOutKeepsABigEnoughCard()
    {
        var flow = FlowOf(new GroupNode("g", ["m1", "m2", "m3"]), Step("m1"), Step("m2"), Step("m3"));

        var taken = FlowEdits.TakeOut(flow, "m3").Flow;

        Assert.Equal(["m1", "m2"], ((GroupNode)taken.FindNode("g")!).Members);
    }

    [Fact]
    public void RunTogetherOnAStepMakesACard()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c"));

        var (result, refusal) = FlowEdits.RunTogether(flow, "c", "b");

        Assert.Equal(ConnectRefusal.None, refusal);
        Assert.Equal(["b", "c"], ((GroupNode)result.FindNode("together")!).Members);
        Assert.Equal([E("a")], After(result, "together"));
    }

    [Fact]
    public void RunTogetherMovesAMemberBetweenCards()
    {
        var flow = FlowOf(
            new GroupNode("g", ["m1", "m2", "m3"]), Step("m1"), Step("m2"), Step("m3"),
            new GroupNode("h", ["n1", "n2"]), Step("n1"), Step("n2"));

        var (result, _) = FlowEdits.RunTogether(flow, "m3", "n1");

        Assert.Equal(["m1", "m2"], ((GroupNode)result.FindNode("g")!).Members);
        Assert.Equal(["n1", "n2", "m3"], ((GroupNode)result.FindNode("h")!).Members);
    }

    [Fact]
    public void RunAfterMakesItWaitForTheTargetOnly()
    {
        var flow = FlowOf(Step("a"), Step("b"), Step("c", "a"));

        var (result, _) = FlowEdits.RunAfter(flow, "c", "b");

        Assert.Equal([E("b")], After(result, "c"));
    }

    [Fact]
    public void RunAfterRefusesALoop()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"));

        var (result, refusal) = FlowEdits.RunAfter(flow, "a", "b");

        Assert.Equal(ConnectRefusal.Loop, refusal);
        Assert.Same(flow, result);
    }

    [Fact]
    public void RunAfterAMemberWaitsForItsCard()
    {
        var flow = FlowOf(new GroupNode("g", ["m1", "m2"]), Step("m1"), Step("m2"), Step("s"));

        Assert.Equal([E("g")], After(FlowEdits.RunAfter(flow, "s", "m2").Flow, "s"));
    }
}
