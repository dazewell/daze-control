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

    /// <summary>"Failure ignored": counts as done, dependents run.</summary>
    Ignore,
}

/// <summary>An incoming edge, stored on its target node as <c>after:</c> (ARCHITECTURE → Config).</summary>
public sealed record Edge(string From, EdgeWhen When = EdgeWhen.Success);

/// <summary>A step or a card. Ids are unique within a flow.</summary>
public abstract record Node(string Id)
{
    /// <summary>Label on the canvas; null = the provider's title for a step, "Run together" for a card.</summary>
    public string? Name { get; init; }

    public ValueList<Edge> After { get; init; } = [];

    /// <summary>Null = inherit: the step type's default (D-080), else <see cref="FailurePolicy.Stop"/>.</summary>
    public FailurePolicy? OnFailure { get; init; }
}

/// <summary>One provider operation (<paramref name="Step"/> = descriptor id, e.g. "bluetooth.connect").</summary>
public sealed record StepNode(string Id, string Step) : Node(Id)
{
    public ValueDictionary<ParamValue> With { get; init; }

    /// <summary>Null = inherit: the step type's default (D-080), else the provider's default timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Extra attempts after the first; null = inherit, else 0.</summary>
    public int? RetryCount { get; init; }

    /// <summary>Wait between attempts; null = inherit, else <see cref="StepSettings.DefaultRetryDelay"/>.</summary>
    public TimeSpan? RetryDelay { get; init; }

    /// <summary>"Only if" (D-081): a <see cref="OnlyAlways"/> or a <c>ConditionSpec.Id</c> of the step type. Null = absent = always.</summary>
    public string? Only { get; init; }

    /// <summary>False = turned off (<c>enabled: false</c>, T25.4): the step does nothing and counts as done; its success
    /// and "always" links fire, its failure links never do, and it gives no values.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The explicit "run it every time" (kept in YAML so a later type default can't override it, B8).</summary>
    public const string OnlyAlways = "always";
}

/// <summary>
/// A card: <paramref name="Members"/> (ids of step nodes in the same flow, no nesting) start together; the card
/// finishes when all have. Members carry no edges of their own (D-003).
/// </summary>
public sealed record GroupNode(string Id, ValueList<string> Members) : Node(Id);
