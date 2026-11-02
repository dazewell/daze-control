using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class FlowCommandsTests
{
    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes]);

    private static ValueList<Edge> After(Flow flow, string id) => flow.FindNode(id)!.After;

    private static Edge E(string from, EdgeWhen when = EdgeWhen.Success) => new(from, when);

    [Fact]
    public void AddNamesTheStepAfterItsOperationAndRunsItAfterTheAnchors()
    {
        var flow = FlowOf(Step("a"), new GroupNode("card", ["m1", "m2"]), Step("m1"), Step("m2"), new StepNode("connect", "wait"));

        var (added, id) = FlowCommands.Add(flow, "bluetooth.connect", ["m1", "a", "nope"]);

        Assert.Equal("connect-2", id);
        var step = Assert.IsType<StepNode>(added.Nodes[^1]);
        Assert.Equal("bluetooth.connect", step.Step);
        Assert.Equal([E("a"), E("card")], step.After);
    }

    [Fact]
    public void InsertSplitsTheEdgeAndKeepsItsCondition()
    {
        var flow = FlowOf(Step("a"), Step("b") with { After = [E("a", EdgeWhen.Failure)] });

        var (inserted, id) = FlowCommands.Insert(flow, "notify", "a", "b");

        Assert.Equal("notify", id);
        Assert.Equal([E("a", EdgeWhen.Failure)], After(inserted, "notify"));
        Assert.Equal([E("notify")], After(inserted, "b"));
        Assert.Same(flow, FlowCommands.Insert(flow, "notify", "b", "a").Flow);
    }

    [Fact]
    public void DeleteReconnectsAroundTheStep()
    {
        // a → b → c, a → b → d (if it fails): deleting b makes c wait for a; the fallback goes with b.
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"), Step("d") with { After = [E("b", EdgeWhen.Failure)] });

        var deleted = FlowCommands.Delete(flow, ["b"]);

        Assert.Equal(["a", "c", "d"], deleted.Nodes.Select(n => n.Id));
        Assert.Equal([E("a")], After(deleted, "c"));
        Assert.Empty(After(deleted, "d"));
    }

    [Fact]
    public void DeleteBridgesChainsAndTakesTheDeletedEdgesCondition()
    {
        // a →(always) b → c → d: deleting b and c makes d wait for a, whatever happens.
        var flow = FlowOf(Step("a"), Step("b") with { After = [E("a", EdgeWhen.Always)] }, Step("c", "b"), Step("d", "c", "a"));

        var deleted = FlowCommands.Delete(flow, ["b", "c"]);

        Assert.Equal([E("a", EdgeWhen.Always)], After(deleted, "d"));
    }

    [Fact]
    public void DeletingACardDeletesItsMembers()
    {
        var flow = FlowOf(Step("a"), new GroupNode("card", ["m1", "m2"]) { After = [E("a")] }, Step("m1"), Step("m2"), Step("z", "card"));

        var deleted = FlowCommands.Delete(flow, ["card"]);

        Assert.Equal(["a", "z"], deleted.Nodes.Select(n => n.Id));
        Assert.Equal([E("a")], After(deleted, "z"));
    }

    [Fact]
    public void DeletingAMemberDissolvesACardLeftWithOneStep()
    {
        var flow = FlowOf(Step("a"), new GroupNode("card", ["m1", "m2"]) { After = [E("a")] }, Step("m1"), Step("m2"), Step("z", "card"));

        var deleted = FlowCommands.Delete(flow, ["m1"]);

        Assert.Equal(["a", "m2", "z"], deleted.Nodes.Select(n => n.Id));
        Assert.Equal([E("a")], After(deleted, "m2"));
        Assert.Equal([E("m2")], After(deleted, "z"));
    }

    [Fact]
    public void DeletingEveryMemberDeletesTheCard()
    {
        var flow = FlowOf(Step("a"), new GroupNode("card", ["m1", "m2"]) { After = [E("a")] }, Step("m1"), Step("m2"), Step("z", "card"));

        var deleted = FlowCommands.Delete(flow, ["m1", "m2"]);

        Assert.Equal(["a", "z"], deleted.Nodes.Select(n => n.Id));
        Assert.Equal([E("a")], After(deleted, "z"));
        Assert.Same(flow, FlowCommands.Delete(flow, ["nope"]));
    }

    [Fact]
    public void PasteAfterTheSelectionKeepsInnerEdgesAndRenames()
    {
        // Copy b → c from a → b → c, paste with c selected: b-2 → c-2, b-2 waits for c.
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("c", "b"));

        var clip = FlowCommands.Copy(flow, ["c", "b"]);
        var (pasted, ids) = FlowCommands.Paste(flow, clip, ["c"]);

        Assert.Equal(["b-2", "c-2"], ids);
        Assert.Equal([E("c")], After(pasted, "b-2"));
        Assert.Equal([E("b-2")], After(pasted, "c-2"));
        Assert.False(FlowGraph.HasLoop(pasted));
    }

    [Fact]
    public void DuplicateKeepsEdgesFromStepsStillThere()
    {
        var flow = FlowOf(Step("a"), Step("b", "a"), Step("b-2", "a"));

        var (duplicated, ids) = FlowCommands.Paste(flow, FlowCommands.Copy(flow, ["b"]), after: null);

        Assert.Equal(["b-3"], ids);
        Assert.Equal([E("a")], After(duplicated, "b-3"));
        // Pasted elsewhere, edges to missing steps are dropped.
        var (other, _) = FlowCommands.Paste(FlowOf(Step("x")), FlowCommands.Copy(flow, ["b"]), after: null);
        Assert.Empty(After(other, "b"));
    }

    [Fact]
    public void CopyingACardCopiesItsMembers()
    {
        var flow = FlowOf(Step("a"), new GroupNode("card", ["m1", "m2"]) { After = [E("a")], OnFailure = FailurePolicy.Skip }, Step("m1"), Step("m2"));

        var (pasted, ids) = FlowCommands.Paste(flow, FlowCommands.Copy(flow, ["card"]), after: null);

        Assert.Equal(["card-2"], ids);
        var card = Assert.IsType<GroupNode>(pasted.FindNode("card-2"));
        Assert.Equal(["m1-2", "m2-2"], card.Members);
        Assert.Equal([E("a")], card.After);
        Assert.Equal(FailurePolicy.Skip, card.OnFailure);
        Assert.Empty(After(pasted, "m1-2"));
    }

    [Fact]
    public void AMemberCopiedAloneLeavesItsCard()
    {
        var flow = FlowOf(Step("a"), new GroupNode("card", ["m1", "m2"]) { After = [E("a")], OnFailure = FailurePolicy.Skip }, Step("m1"), Step("m2"));

        var clip = FlowCommands.Copy(flow, ["m1"]);

        var step = Assert.Single(clip.Nodes);
        Assert.Equal([E("a")], step.After);
        Assert.Equal(FailurePolicy.Skip, step.OnFailure);
        var (pasted, ids) = FlowCommands.Paste(flow, clip, after: null);
        Assert.Equal(["m1-2"], ids);
        Assert.Equal(2, ((GroupNode)pasted.FindNode("card")!).Members.Count);
    }
}
