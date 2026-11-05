using DazeControl.Core.Catalog;

namespace DazeControl.Core.Model;

/// <summary>A step's timing and failure handling with every field decided.</summary>
public sealed record StepSettings(TimeSpan Timeout, int RetryCount, TimeSpan RetryDelay, FailurePolicy OnFailure, string? Only = null)
{
    /// <summary>Wait between tries when nothing says otherwise (what a retry without a delay always meant).</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.Zero;

    /// <summary>The step's own value, else the type default's, else the built-in; each field alone (D-080).</summary>
    public static StepSettings Resolve(StepNode step, ProviderDescriptor? descriptor, StepDefault? typeDefault) => new(
        step.Timeout ?? typeDefault?.Timeout ?? descriptor?.DefaultTimeout ?? TimeSpan.FromSeconds(30),
        step.RetryCount ?? typeDefault?.RetryCount ?? 0,
        step.RetryDelay ?? typeDefault?.RetryDelay ?? DefaultRetryDelay,
        step.OnFailure ?? typeDefault?.OnFailure ?? FailurePolicy.Stop,
        step.Only ?? typeDefault?.Only);
}

/// <summary>Fills a flow's inherited fields from <c>settings.stepDefaults</c>, like <see cref="FlowInputs"/>.</summary>
public static class StepDefaults
{
    /// <summary>
    /// Every step gets its resolved timeout, retry and policy, every card its policy, so the scheduler and runner
    /// read plain values.
    /// </summary>
    public static Flow Apply(Flow flow, ValueDictionary<StepDefault> defaults, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        catalog ??= ProviderCatalog.All;
        var nodes = new List<Node>(flow.Nodes.Count);
        foreach (var node in flow.Nodes)
        {
            var filled = node switch
            {
                StepNode step => Fill(step, defaults, catalog),
                _ => node.OnFailure is null ? node with { OnFailure = FailurePolicy.Stop } : node,
            };
            nodes.Add(filled);
        }
        return flow with { Nodes = new(nodes) };
    }

    private static StepNode Fill(StepNode step, ValueDictionary<StepDefault> defaults, IReadOnlyList<ProviderDescriptor> catalog)
    {
        StepDefault? typeDefault = defaults.TryGetValue(step.Step, out var found) ? found : null;
        var descriptor = catalog.FirstOrDefault(d => d.Id == step.Step);
        var resolved = StepSettings.Resolve(step, descriptor, typeDefault);
        // An unknown step type has no timeout to resolve; leave it for the runner to fail with its own message.
        return descriptor is null && step.Timeout is null && typeDefault?.Timeout is null
            ? step with { RetryCount = resolved.RetryCount, RetryDelay = resolved.RetryDelay, OnFailure = resolved.OnFailure, Only = resolved.Only }
            : step with { Timeout = resolved.Timeout, RetryCount = resolved.RetryCount, RetryDelay = resolved.RetryDelay, OnFailure = resolved.OnFailure, Only = resolved.Only };
    }
}
