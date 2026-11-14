using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Runtime.Menus;

/// <summary>How asking a flow's inputs went: the flow with its answers filled in, or a note for why the run must not start.</summary>
/// <param name="Flow">The flow with every <c>$name</c> resolved (answers, else defaults); null with <paramref name="Note"/>.</param>
/// <param name="Note">Why the run ends before it starts ("No answer, so it didn't run."), for history.</param>
public sealed record InputAnswer(Flow? Flow, string? Note);

/// <summary>
/// Ask-at-start (M20, D-107, D-064): before a run, an <c>ask: true</c> input still used by a step is offered as a menu of
/// the Library items of its kind, on the same no-activate window as <c>menu.show</c>. The pick fills <c>$name</c> exactly as
/// a <c>flow.run</c> would. Nothing to choose from (fewer than two items) asks nothing; a timeout follows the input's
/// <c>onTimeout</c> (default: the default device when a person started the run, else stop). Never blocks a slot for long.
/// </summary>
/// <param name="presenter">The window, behind a <see cref="GatedMenuPresenter"/>.</param>
public sealed class InputPrompts(IMenuPresenter presenter)
{
    public async Task<InputAnswer> AskAsync(Flow flow, Library library, string trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(library);
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, input) in flow.Inputs.Where(i => i.Value.Ask && FlowInputs.IsUsed(flow, i.Key)))
        {
            if (input.Kind is not { } kind)
                continue; // text is never asked (T23.7; validation refuses it)
            var items = ItemsOf(library, kind);
            if (items.Count < 2)
                continue; // nothing to choose between: the default as always
            MenuOutcome outcome;
            try
            {
                var request = new MenuRequest(input.Label, items, MenuLayout.List, Large: false, input.Timeout ?? Prompts.DefaultTimeout,
                    items.Any(i => i.FlowId == input.Default) ? input.Default : null);
                outcome = await presenter.ShowAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new(null, "Cancelled while asking.");
            }
            catch (StepFailedException e)
            {
                return new(null, e.Message); // a full-screen game has the screen (#32)
            }
            if (outcome.Reason == MenuCloseReason.Picked && outcome.FlowId is { } key && items.Any(i => i.FlowId == key))
            {
                answers[name] = key;
            }
            else if (outcome.Reason == MenuCloseReason.TimedOut)
            {
                if (Prompts.OnTimeoutOf(input.OnTimeout, trigger, Prompts.UseDefault) == Prompts.Stop)
                    return new(null, Prompts.NoAnswer);
            }
            else
            {
                return new(null, "Not answered, so it didn't run.");
            }
        }
        return new(FlowInputs.Apply(flow, answers), null);
    }

    /// <summary>The Library's items of a kind as menu rows (key, name); also what a Pick step offers (D-117).</summary>
    internal static List<MenuItem> ItemsOf(Library library, LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => [.. library.Audio.Select(d => new MenuItem(d.Key, d.Key, MenuItemState.Ready)), .. BoundItems(library, kind)],
        LibraryKind.Bluetooth => [.. library.Bluetooth.Select(d => new MenuItem(d.Key, d.Name ?? d.Key, MenuItemState.Ready)), .. BoundItems(library, kind)],
        LibraryKind.Bound => [.. BoundItems(library, kind)],
        LibraryKind.Display => [.. library.Displays.Select(d => new MenuItem(d.Key, d.Key, MenuItemState.Ready))],
        _ => [.. library.Android.Select(d => new MenuItem(d.Key, d.Key, MenuItemState.Ready))],
    };

    /// <summary>Bound devices whose side of this kind is in the Library (D-116).</summary>
    private static IEnumerable<MenuItem> BoundItems(Library library, LibraryKind kind) => library.Bound
        .Where(b => library.Contains(kind, b.Key))
        .Select(b => new MenuItem(b.Key, b.Name ?? b.Key, MenuItemState.Ready));
}
