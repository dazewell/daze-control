using System.Collections.Concurrent;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Control;
using CtrlAltDaze.Runtime.Menus;

namespace CtrlAltDaze.Runtime.Providers;

/// <summary>
/// <c>menu.show</c> (M12, DESIGN §2, §3.2): builds the menu from the current flows, shows it through the presenter and
/// launches the pick the way the tray does (trigger "Menu · Couch"), or with <c>wait</c> runs it nested and takes its
/// outcome. Nothing picked ends the flow quietly. Dry runs never reach it (<see cref="EngineController"/>'s dry
/// provider says "Would show N choices"); a step test shows the menu but runs nothing.
/// </summary>
/// <param name="presenter">The window, behind a <see cref="GatedMenuPresenter"/>.</param>
/// <param name="flows">The engine's flows and launcher.</param>
public sealed class MenuProvider(IMenuPresenter presenter, IFlowStarter flows) : IStepProvider
{
    private readonly ConcurrentDictionary<string, string> _lastPicked = new(StringComparer.Ordinal);

    public string Id => ProviderCatalog.MenuShowId;

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = ItemsOf(context.Node, flows);
        if (items.Count == 0)
            throw new StepFailedException("None of this menu's flows are there anymore. Choose others in the menu step.");
        var key = context.Flow.Id + "/" + context.Node.Id;
        _lastPicked.TryGetValue(key, out var last);
        var request = new MenuRequest(Text(context.Node, "title") is { Length: > 0 } title ? title : context.Flow.Name, items,
            Text(context.Node, "layout") == "grid" ? MenuLayout.Grid : MenuLayout.List, Text(context.Node, "size") == "large",
            CloseAfterOf(context.Node), last);

        var outcome = await presenter.ShowAsync(request, cancellationToken).ConfigureAwait(false);

        if (outcome.FlowId is not { } picked)
            throw new StepEndsFlowException("Nothing picked");
        var item = items.First(i => i.FlowId == picked);
        if (!item.CanRun)
            throw new StepFailedException($"{item.Name} can't run right now.");
        _lastPicked[key] = picked;
        context.SetValue("picked", item.Name);

        if (context.Flow.Id.StartsWith(EngineController.TestPrefix, StringComparison.Ordinal))
        {
            context.SetSummary($"You picked {item.Name}. Testing doesn't run it.");
            return;
        }
        context.SetSummary($"Picked {item.Name}");
        if (bool.TryParse(Text(context.Node, "wait"), out var wait) && wait)
        {
            if (context.RunNested is not { } nested)
                throw new StepFailedException("This menu can't wait for a flow here.");
            var answer = await nested(picked, cancellationToken).ConfigureAwait(false);
            if (answer.Quiet)
                throw new StepEndsFlowException("Nothing picked");
            if (answer.Error is { } error)
            {
                cancellationToken.ThrowIfCancellationRequested(); // a timeout or stop keeps its own words
                throw new StepFailedException(error);
            }
            return;
        }
        // A person's pick starts a fresh call chain: menus may point at each other ("Back"), there is no recursion (§2.5).
        var started = flows.Start(picked, "Menu · " + context.Flow.Name, [], new Dictionary<string, string>(), context.Flow.Name);
        if (!started.Started)
            throw new StepFailedException(started.Message ?? "It didn't start.");
        if (started.Message is not null)
            context.SetSummary($"{item.Name} was already running, so this pick was ignored.");
    }

    /// <summary>The choices: unknown flows and repeats dropped (a warning at validation, DESIGN §2.5), state from the flows now.</summary>
    internal static List<MenuItem> ItemsOf(StepNode node, IFlowStarter flows)
    {
        List<MenuItem> items = [];
        foreach (var id in IdsOf(node))
        {
            var called = flows.Resolve(id);
            if (called.Why == CalledWhy.Missing)
                continue;
            var state = called.Flow is not null ? (flows.IsRunning(id) ? MenuItemState.Running : MenuItemState.Ready)
                : called.Why == CalledWhy.TurnedOff ? MenuItemState.TurnedOff : MenuItemState.NeedsFixing;
            items.Add(new MenuItem(id, called.Name, state));
        }
        return items;
    }

    /// <summary>The written flow ids without repeats (the second of a repeat is dropped).</summary>
    internal static List<string> IdsOf(StepNode node)
    {
        IEnumerable<string> ids = node.With.TryGetValue("items", out var value)
            ? value switch
            {
                ListParam list => list.Items.Select(i => i.ToString() ?? ""),
                ScalarParam scalar => [scalar.Text],
                _ => [],
            }
            : [];
        return [.. ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    private static string? Text(StepNode node, string name) =>
        node.With.TryGetValue(name, out var value) && value is ScalarParam scalar ? scalar.Text : null;

    private static TimeSpan CloseAfterOf(StepNode node)
    {
        var text = Text(node, "closeAfter");
        if (text is null)
            return TimeSpan.FromSeconds(30);
        return Duration.TryParse(text, out var span) ? span : throw new StepFailedException($"\"{text}\" isn't a duration (like 30s, 2m).");
    }
}
