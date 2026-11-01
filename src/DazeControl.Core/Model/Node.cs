namespace DazeControl.Core.Model;

public enum EdgeWhen
{
    Success,
    Failure,
    Always,
}

/// <summary>What a node's failure does to the rest of the flow (FEATURES F1).</summary>
public enum FailurePolicy
{
    /// <summary>Cancel the flow.</summary>
    Stop,

    /// <summary>Skip what depends on this node; other branches go on.</summary>
    Skip,

    /// <summary>"May fail": counts as done, dependents run.</summary>
    Ignore,
}

/// <summary>An incoming edge, stored on its target node as <c>after:</c> (ARCHITECTURE → Config).</summary>
public sealed record Edge(string From, EdgeWhen When = EdgeWhen.Success);

/// <summary><paramref name="Count"/> extra attempts after the first, <paramref name="Delay"/> apart.</summary>
public sealed record RetryPolicy(int Count, TimeSpan Delay);

/// <summary>A step or a card. Ids are unique within a flow.</summary>
public abstract record Node(string Id)
{
    public ValueList<Edge> After { get; init; } = [];

    public FailurePolicy OnFailure { get; init; } = FailurePolicy.Stop;
}

/// <summary>One provider operation (<paramref name="Step"/> = descriptor id, e.g. "bluetooth.connect").</summary>
public sealed record StepNode(string Id, string Step) : Node(Id)
{
    public ValueDictionary<ParamValue> With { get; init; }

    /// <summary>Null = the provider's default timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    public RetryPolicy? Retry { get; init; }
}

/// <summary>
/// A card: <paramref name="Members"/> (ids of step nodes in the same flow, no nesting) start together; the card
/// finishes when all have. Members carry no edges of their own (D-003).
/// </summary>
public sealed record GroupNode(string Id, ValueList<string> Members) : Node(Id)
{
    /// <summary>Card header; null = "Run together".</summary>
    public string? Name { get; init; }
}
