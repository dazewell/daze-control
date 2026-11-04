using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Runtime.Control;

namespace DazeControl.Runtime.Providers;

/// <summary>
/// <c>flow.run</c> in start mode (<c>wait: false</c>, D-054): starts the flow the way the tray does and succeeds once
/// the start was accepted. A call that waits never reaches a provider: the runner runs it nested (D-062).
/// </summary>
public sealed class FlowProvider(IFlowStarter flows) : IStepProvider
{
    public string Id => ProviderCatalog.RunFlowId;

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = ConfigValidator.CalledFlow(context.Node) ?? "";
        var inputs = FlowInputs.Given(context.Node);
        if (flows.Resolve(target) is { Flow: { } called } resolved
            && FlowInputs.Check(called, resolved.Library, inputs, context.Flow.Name) is { } missing)
            return Task.FromException(new StepFailedException(missing));
        var answer = flows.Start(target, "Flow · " + context.Flow.Name, context.Chain, inputs);
        return answer.Started ? Task.CompletedTask : Task.FromException(new StepFailedException(answer.Message ?? "It didn't start."));
    }
}
