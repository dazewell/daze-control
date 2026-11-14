using System.Globalization;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Menus;

namespace CtrlAltDaze.Runtime.Providers;

/// <summary>
/// <c>prompt.ask</c> (M20, D-107): asks the person a Yes/No or a choose-one question on the no-activate menu window
/// (the same <see cref="IMenuPresenter"/> seam as <c>menu.show</c>, so it never takes focus and a controller works).
/// Yes goes on and No fails the step like a check step; a chosen answer turns on the flag <c>name:answer</c>. No answer
/// in time follows <c>onTimeout</c>: <c>stop</c> (the default, "No answer" in history), <c>use-default</c> or <c>skip</c>.
/// </summary>
/// <param name="presenter">The window, behind a <see cref="GatedMenuPresenter"/>.</param>
/// <param name="setFlag">Sets a named flag (the engine's flag store); null = the answer isn't remembered.</param>
public sealed class PromptProvider(IMenuPresenter presenter, Action<string, bool>? setFlag = null) : IStepProvider
{
    private const string YesKey = "yes", NoKey = "no";

    public string Id => ProviderCatalog.PromptAskId;

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var node = context.Node;
        var choose = Text(node, "mode") == Prompts.Choose;
        var answers = choose ? AnswersOf(node) : [];
        if (choose && (answers.Count < 2 || answers.Count > Prompts.MaxChoices))
            throw new StepFailedException($"Choose needs 2 to {Prompts.MaxChoices} answers. Fix them in the Ask step.");
        var fallback = Text(node, "default");
        List<MenuItem> items = choose
            ? [.. answers.Select((a, i) => new MenuItem(Key(i), a, MenuItemState.Ready))]
            : [new(YesKey, "Yes", MenuItemState.Ready), new(NoKey, "No", MenuItemState.Ready)];
        var request = new MenuRequest(Text(node, "question") ?? "", items, MenuLayout.List, Large: false, TimeoutOf(node),
            DefaultKey(fallback, choose, answers));

        var outcome = await presenter.ShowAsync(request, cancellationToken).ConfigureAwait(false);

        string picked;
        if (outcome.Reason == MenuCloseReason.Picked && outcome.FlowId is { } key)
        {
            picked = key;
        }
        else if (outcome.Reason == MenuCloseReason.TimedOut)
        {
            var what = Prompts.OnTimeoutOf(Text(node, "onTimeout"), context.Trigger, Prompts.Stop);
            if (what == Prompts.Skip)
            {
                context.SetSummary("No answer, carried on");
                return;
            }
            if (what == Prompts.UseDefault && DefaultKey(fallback, choose, answers) is { } use)
                picked = use;
            else
                throw new StepEndsFlowException("No answer");
        }
        else
        {
            throw new StepEndsFlowException("Not answered"); // closed without an answer (Esc, a click away) or stopped
        }

        if (!choose)
        {
            var yes = picked == YesKey;
            if (Text(node, "flag") is { Length: > 0 } yesNo)
                setFlag?.Invoke(yesNo, yes);
            context.SetValue("answer", yes ? "Yes" : "No");
            if (!yes)
                throw new StepFailedException("You answered No.");
            context.SetSummary("Answered Yes");
            return;
        }
        var chosen = answers[int.Parse(picked, CultureInfo.InvariantCulture)];
        if (Text(node, "flag") is { Length: > 0 } flag)
        {
            foreach (var answer in answers)
                setFlag?.Invoke(flag + ":" + answer, answer == chosen);
        }
        context.SetValue("answer", chosen);
        context.SetSummary($"Chose {chosen}");
    }

    /// <summary>What a dry run says: nothing is shown, and the default (else the first answer, else Yes) is assumed.</summary>
    public static string DrySummary(StepNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var answers = Text(node, "mode") == Prompts.Choose ? AnswersOf(node) : [];
        var fallback = Text(node, "default");
        var assumed = answers.Count == 0
            ? string.Equals(fallback, NoKey, StringComparison.OrdinalIgnoreCase) ? "No" : "Yes"
            : answers.FirstOrDefault(a => a.Equals(fallback, StringComparison.OrdinalIgnoreCase)) ?? answers[0];
        return $"Would ask \"{Text(node, "question")}\", assuming {assumed}";
    }

    /// <summary>The menu key of the default answer, or null when none is set (or it isn't one of the answers).</summary>
    private static string? DefaultKey(string? fallback, bool choose, List<string> answers)
    {
        if (string.IsNullOrEmpty(fallback))
            return null;
        if (!choose)
            return fallback.Equals(YesKey, StringComparison.OrdinalIgnoreCase) ? YesKey : fallback.Equals(NoKey, StringComparison.OrdinalIgnoreCase) ? NoKey : null;
        var at = answers.FindIndex(a => a.Equals(fallback, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? null : Key(at);
    }

    private static string Key(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static List<string> AnswersOf(StepNode node) =>
        node.With.TryGetValue("choices", out var value) && value is ListParam list
            ? [.. list.Items.OfType<ScalarParam>().Select(i => i.Text.Trim()).Where(t => t.Length > 0)]
            : [];

    private static string? Text(StepNode node, string name) =>
        node.With.TryGetValue(name, out var value) && value is ScalarParam scalar ? scalar.Text.Trim() : null;

    private static TimeSpan TimeoutOf(StepNode node) =>
        Text(node, "timeout") is not { } text ? Prompts.DefaultTimeout
            : Duration.TryParse(text, out var span) ? span : throw new StepFailedException($"\"{text}\" isn't a duration (like 30s, 2m).");
}
