using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Runtime.Menus;

namespace DazeControl.Runtime.Providers;

/// <summary>
/// <c>prompt.pick</c> (D-116): offers the Library's items of one kind on the no-activate menu window (as <c>prompt.ask</c>,
/// so it never takes focus) and keeps the one picked as the step's result; later steps use it as <c>$result:id</c>.
/// One item is picked without asking; none fails the step. No answer in time follows <c>onTimeout</c>: <c>stop</c>
/// (default, "No answer" in history) or <c>use-default</c>. Closing it without a pick ends the run quietly.
/// </summary>
/// <param name="presenter">The window, behind a <see cref="GatedMenuPresenter"/>.</param>
public sealed class PickProvider(IMenuPresenter presenter) : IStepProvider
{
    public string Id => ProviderCatalog.PromptPickId;

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var node = context.Node;
        if (PickResults.KindOf(node) is not { } kind)
            throw new StepFailedException("Choose what to pick in the Pick step.");
        var items = InputPrompts.ItemsOf(context.Library, kind);
        if (items.Count == 0)
            throw new StepFailedException($"Your Library has no {Library.KindName(kind)} to pick.");
        var fallback = Text(node, "default") is { Length: > 0 } d && items.Any(i => i.FlowId == d) ? d : null;
        if (items.Count == 1)
        {
            Picked(context, items[0]);
            return;
        }
        var question = Text(node, "question") is { Length: > 0 } q ? q : QuestionFor(kind);
        var outcome = await presenter.ShowAsync(new MenuRequest(question, items, MenuLayout.List, Large: false, TimeoutOf(node), fallback),
            cancellationToken).ConfigureAwait(false);
        if (outcome.Reason == MenuCloseReason.Picked && items.FirstOrDefault(i => i.FlowId == outcome.FlowId) is { } item)
        {
            Picked(context, item);
            return;
        }
        if (outcome.Reason == MenuCloseReason.TimedOut)
        {
            if (Prompts.OnTimeoutOf(Text(node, "onTimeout"), context.Trigger, Prompts.Stop) == Prompts.UseDefault
                && items.FirstOrDefault(i => i.FlowId == fallback) is { } use)
            {
                Picked(context, use);
                return;
            }
            throw new StepEndsFlowException("No answer");
        }
        throw new StepEndsFlowException("Not answered"); // closed without a pick (Esc, a click away) or stopped
    }

    /// <summary>What a dry run says and keeps: nothing is shown; the default, else the first item, is assumed.</summary>
    public static string DryRun(StepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kind = PickResults.KindOf(context.Node) ?? LibraryKind.Audio;
        var items = InputPrompts.ItemsOf(context.Library, kind);
        if (items.Count == 0)
            throw new StepFailedException($"Your Library has no {Library.KindName(kind)} to pick.");
        var item = items.FirstOrDefault(i => i.FlowId == Text(context.Node, "default")) ?? items[0];
        context.SetResult(item.FlowId);
        var question = Text(context.Node, "question") is { Length: > 0 } q ? q : QuestionFor(kind);
        return $"Would ask \"{question}\", assuming {item.Name}";
    }

    /// <summary>"Pick an audio device".</summary>
    public static string QuestionFor(LibraryKind kind) =>
        $"Pick {(kind is LibraryKind.Audio or LibraryKind.Android ? "an" : "a")} {Library.KindName(kind)}";

    private static void Picked(StepContext context, MenuItem item)
    {
        context.SetResult(item.FlowId);
        context.SetSummary($"Picked {item.Name}");
    }

    private static string? Text(StepNode node, string name) =>
        node.With.TryGetValue(name, out var value) && value is ScalarParam scalar ? scalar.Text.Trim() : null;

    private static TimeSpan TimeoutOf(StepNode node) =>
        Text(node, "timeout") is not { } text ? Prompts.DefaultTimeout
            : Duration.TryParse(text, out var span) ? span : throw new StepFailedException($"\"{text}\" isn't a duration (like 30s, 2m).");
}
