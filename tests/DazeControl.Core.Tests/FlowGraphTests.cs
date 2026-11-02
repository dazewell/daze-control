using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class FlowGraphTests
{
    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    /// <summary>a → b → c, a card g (m1, m2) after c, and a loose d.</summary>
    private static readonly Flow Chain = new("f", "F",
    [
        Step("a"), Step("b", "a"), Step("c", "b"),
        new GroupNode("g", ["m1", "m2"]) { After = [new Edge("c")] }, Step("m1"), Step("m2"), Step("d"),
    ]);

    [Theory]
    [InlineData("a", "d", ConnectRefusal.None)]
    [InlineData("d", "a", ConnectRefusal.None)]
    [InlineData("a", "c", ConnectRefusal.None)]
    [InlineData("a", "b", ConnectRefusal.Exists)]
    [InlineData("c", "a", ConnectRefusal.Loop)]
    [InlineData("b", "b", ConnectRefusal.Loop)]
    [InlineData("m1", "a", ConnectRefusal.Loop)]
    [InlineData("m1", "m2", ConnectRefusal.SameCard)]
    [InlineData("m1", "g", ConnectRefusal.SameCard)]
    [InlineData("m2", "d", ConnectRefusal.None)]
    [InlineData("x", "a", ConnectRefusal.Unknown)]
    public void CanConnectRefusesLoopsAndInsideCards(string from, string to, ConnectRefusal expected) =>
        Assert.Equal(expected, FlowGraph.CanConnect(Chain, from, to));

    [Fact]
    public void ConnectAddsTheEdgeOnTheTargetsCard()
    {
        var flow = FlowGraph.Connect(Chain, "d", "m2", EdgeWhen.Failure);

        Assert.Equal([new Edge("c"), new Edge("d", EdgeWhen.Failure)], flow.FindNode("g")!.After);
        Assert.Empty(flow.FindNode("m2")!.After);
    }

    [Fact]
    public void ConnectFromAMemberStartsAtItsCard()
    {
        var flow = FlowGraph.Connect(Chain, "m1", "d");

        Assert.Equal([new Edge("g")], flow.FindNode("d")!.After);
    }

    [Fact]
    public void RefusedConnectLeavesTheFlowAlone() =>
        Assert.Same(Chain, FlowGraph.Connect(Chain, "c", "a"));

    [Fact]
    public void DownstreamIsEverythingThatWaits() =>
        Assert.Equal(["c", "g"], FlowGraph.Downstream(Chain, "b").Order());

    [Fact]
    public void DisconnectAndSetWhenWorkOnDrawnEdges()
    {
        var flow = FlowGraph.SetWhen(Chain, "c", "g", EdgeWhen.Always);
        Assert.Equal([new Edge("c", EdgeWhen.Always)], flow.FindNode("g")!.After);

        flow = FlowGraph.Disconnect(flow, "c", "g");
        Assert.Empty(flow.FindNode("g")!.After);
        Assert.Equal([new Edge("b")], flow.FindNode("c")!.After);
    }
}
