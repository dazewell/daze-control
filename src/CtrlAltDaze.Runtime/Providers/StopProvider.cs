using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Control;

namespace CtrlAltDaze.Runtime.Providers;

/// <summary>
/// <c>flow.stop</c> (T24.1, D-133): ends this flow on purpose. "Quietly" is not a failure and shows nothing ("Stopped here" in
/// history); "As failed" fails the step with the message. In a called flow a quiet stop ends only that flow and the caller's
/// step is done; "As failed" fails the caller's step. A step test and a dry run say what would happen and stop nothing.
/// </summary>
public sealed class StopProvider : IStepProvider
{
    public const string Quietly = "quiet", AsFailed = "failed";

    /// <summary>History's words for a quiet stop.</summary>
    public const string StoppedHere = "Stopped here";

    public string Id => ProviderCatalog.StopFlowId;

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = Message(context.Node);
        if (context.Flow.Id.StartsWith(EngineController.TestPrefix, StringComparison.Ordinal))
        {
            context.SetSummary("Testing doesn't stop anything. " + DrySummary(context.Node));
            return Task.CompletedTask;
        }
        if (IsFailed(context.Node))
            throw new StepFailedException(message.Length > 0 ? message : "The flow was stopped here.");
        context.SetSummary(StoppedHere);
        throw new StepEndsFlowException(message.Length > 0 ? $"{StoppedHere}: {message}" : StoppedHere) { IsStop = true };
    }

    /// <summary>What a dry run or a step test says: "Would stop here" or "Would stop here as failed: message".</summary>
    public static string DrySummary(StepNode node) =>
        IsFailed(node) ? "Would stop here as failed" + (Message(node) is { Length: > 0 } m ? ": " + m : "") : "Would stop here";

    private static bool IsFailed(StepNode node) => Text(node, "mode") == AsFailed;

    private static string Message(StepNode node) => Text(node, "message")?.Trim() ?? "";

    private static string? Text(StepNode node, string name) =>
        node.With.TryGetValue(name, out var value) && value is ScalarParam scalar ? scalar.Text : null;
}
