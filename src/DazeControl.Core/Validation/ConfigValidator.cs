using System.Globalization;
using System.Text.RegularExpressions;
using DazeControl.Core.Catalog;
using DazeControl.Core.Input;
using DazeControl.Core.Model;

namespace DazeControl.Core.Validation;

/// <summary>
/// Checks a mapped <see cref="Configuration"/> against FEATURES F1 and the provider catalog. Errors inside a flow
/// carry its id, so the host disables (or keeps the last valid version of) only that flow (D-021).
/// </summary>
public static partial class ConfigValidator
{
    public const int MaxRetries = 10; // the inspector's "Try again" range (UX 5.4)

    /// <summary>Longest timeout or retry delay; timers can't hold much more than 49 days.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    /// <param name="config">The mapped configuration.</param>
    /// <param name="earlier">Problems from loading (<c>ConfigLoad.Problems</c>): flows they reject get no hotkeys.</param>
    /// <param name="catalog">Step types; the compiled-in catalog by default.</param>
    /// <returns>Only the validator's own problems.</returns>
    public static ValueList<Problem> Validate(Configuration config, IEnumerable<Problem>? earlier = null,
        IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        var problems = new List<Problem>();
        catalog ??= ProviderCatalog.All;
        var badLibraryItems = new HashSet<(LibraryKind, string)>();
        foreach (var device in config.Library.Bluetooth)
        {
            if (!BluetoothAddress().IsMatch(device.Address))
            {
                badLibraryItems.Add((LibraryKind.Bluetooth, device.Key));
                problems.Add(new(Severity.Error, $"library.bluetooth.{device.Key}.address",
                    $"\"{device.Address}\" isn't a Bluetooth address (like B8:20:8E:ED:48:FC)."));
            }
        }
        var checkers = config.Flows.Select(f => new FlowChecker(f, config.Library, badLibraryItems, catalog, problems)).ToList();
        foreach (var checker in checkers)
            checker.Check();
        CheckMenus(config.Flows, config.Settings.StepDefaults, problems);
        CheckWaits(config.Flows, config.Settings.StepDefaults, problems);
        CheckCalls(config.Flows, problems);
        CheckPassedInputs(config.Flows, config.Library, badLibraryItems, problems);
        // Second pass, so a flow that is rejected anyway (or turned off) can't take a hotkey from a healthy one.
        var hotkeyOwners = new Dictionary<Hotkey, string>();
        var rejected = FlowsWithErrors(problems.Concat(earlier ?? []));
        foreach (var checker in checkers)
            checker.ClaimHotkeys(hotkeyOwners, rejected);
        return new(problems);
    }

    /// <summary>Flows that must not run as written: an error carries their id.</summary>
    public static IReadOnlySet<string> FlowsWithErrors(IEnumerable<Problem> problems) =>
        problems.Where(p => p is { Severity: Severity.Error, FlowId: not null }).Select(p => p.FlowId!).ToHashSet();

    /// <summary>
    /// The call graph "F runs G" (D-063): every <c>flow.run</c> step, both modes (a start loop restarts itself
    /// forever). Own flow (#49), loops (#50, on every step in the loop) and unknown flows (#51) are errors; a turned-off
    /// flow (#52) or one with errors is a warning. M12's <c>menu.show</c> items with <c>wait: true</c> join here.
    /// </summary>
    private static void CheckCalls(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        var byId = new Dictionary<string, Flow>(StringComparer.Ordinal);
        foreach (var flow in flows)
            byId.TryAdd(flow.Id, flow);
        var calls = flows.SelectMany(f => f.Nodes.OfType<StepNode>()
                .Where(s => s.Step == ProviderCatalog.RunFlowId)
                .Select(s => (Flow: f, Step: s, Target: CalledFlow(s))))
            .Where(c => c.Target is { Length: > 0 })
            .ToList();
        var graph = CallGraph(byId, CallEdges(flows));
        // Errors first, so a flow this pass rejects (a loop) also gets callers' "needs fixing" warnings.
        var warnings = new List<(Flow Flow, string Path, Flow Called)>();
        foreach (var (flow, step, target) in calls)
        {
            var path = $"flows.{flow.Id}.nodes.{step.Id}.with.flow";
            if (target == flow.Id)
                problems.Add(new(Severity.Error, path, "A flow can't run itself.", flow.Id));
            else if (!byId.TryGetValue(target!, out var called))
                problems.Add(new(Severity.Error, path, $"{target} isn't there anymore. Pick another flow.", flow.Id));
            else if (PathBetween(graph, target!, flow.Id) is { } back)
                problems.Add(new(Severity.Error, path, LoopText(byId, flow, back), flow.Id));
            else
                warnings.Add((flow, path, called));
        }
        // #39: a menu that waits for a flow which reaches back to the menu's own flow.
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(IsWaitingMenu))
            {
                foreach (var item in MenuItems(step).Distinct().Where(i => i != flow.Id && byId.ContainsKey(i)))
                {
                    if (PathBetween(graph, item, flow.Id) is { } back)
                        problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.with.items", LoopText(byId, flow, back), flow.Id));
                }
            }
        }
        var broken = FlowsWithErrors(problems);
        foreach (var (flow, path, called) in warnings)
        {
            if (!called.Enabled)
                problems.Add(new(Severity.Warning, path, $"{called.Name} is turned off, so this step will fail. Turn it on in Flows.", flow.Id));
            else if (broken.Contains(called.Id))
                problems.Add(new(Severity.Warning, path, $"{called.Name} needs fixing; this step runs its last working version.", flow.Id));
        }
    }

    private static Dictionary<string, HashSet<string>> CallGraph(Dictionary<string, Flow> byId, IEnumerable<(string From, string To)> calls)
    {
        var graph = byId.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (from, to) in calls.Where(c => c.From != c.To && byId.ContainsKey(c.To)))
            graph[from].Add(to);
        return graph;
    }

    /// <summary>#50: "That would make a loop: Headphones runs Make it the default, which runs Headphones."</summary>
    private static string LoopText(Dictionary<string, Flow> byId, Flow flow, List<string> back) =>
        "That would make a loop: " + flow.Name + string.Concat(back.Select((id, i) =>
            (i == 0 ? " runs " : ", which runs ") + byId[id].Name)) + $", which runs {flow.Name}.";

    /// <summary>
    /// Why <paramref name="callerId"/> can't run <paramref name="targetId"/> (the editor's flow picker, UX §5.4): #49 for
    /// itself, #50 when the target already runs the caller (through any of its <c>flow.run</c> steps); else null.
    /// </summary>
    public static string? CallProblem(IReadOnlyList<Flow> flows, string callerId, string targetId)
    {
        ArgumentNullException.ThrowIfNull(flows);
        if (callerId == targetId)
            return "A flow can't run itself.";
        var byId = new Dictionary<string, Flow>(StringComparer.Ordinal);
        foreach (var flow in flows)
            byId.TryAdd(flow.Id, flow);
        if (!byId.TryGetValue(callerId, out var caller) || !byId.ContainsKey(targetId))
            return null;
        var graph = CallGraph(byId, CallEdges(flows));
        return PathBetween(graph, targetId, callerId) is { } back ? LoopText(byId, caller, back) : null;
    }

    /// <summary>"F runs G": every <c>flow.run</c> step and the items of every <c>menu.show</c> that waits (D-063).</summary>
    private static IEnumerable<(string From, string To)> CallEdges(IReadOnlyList<Flow> flows) =>
        flows.SelectMany(f => f.Nodes.OfType<StepNode>().SelectMany(s =>
            s.Step == ProviderCatalog.RunFlowId && CalledFlow(s) is { Length: > 0 } target ? [(f.Id, target)]
            : IsWaitingMenu(s) ? MenuItems(s).Select(i => (f.Id, i))
            : []));

    private static bool IsWaitingMenu(StepNode step) =>
        step.Step == ProviderCatalog.MenuShowId && step.With.TryGetValue("wait", out var wait)
        && wait is ScalarParam { Text: var text } && bool.TryParse(text, out var on) && on;

    /// <summary>The flow ids a <c>menu.show</c> lists, in order; empty when <c>items</c> isn't a list of plain values (the param check's error).</summary>
    public static IReadOnlyList<string> MenuItems(StepNode step) =>
        step.With.TryGetValue("items", out var value) && value is ListParam list && list.Items.All(i => i is ScalarParam)
            ? [.. list.Items.Cast<ScalarParam>().Select(i => i.Text.Trim())]
            : [];

    private static TimeSpan ResolvedTimeout(StepNode step, ValueDictionary<StepDefault> defaults) =>
        StepSettings.Resolve(step, ProviderCatalog.Find(step.Step), defaults.TryGetValue(step.Step, out var found) ? found : null).Timeout;

    /// <summary>A wait at least as long as its (resolved) time limit would be cut short by the runner.</summary>
    private static void CheckWaits(IReadOnlyList<Flow> flows, ValueDictionary<StepDefault> defaults, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == "wait"))
            {
                if (step.With.TryGetValue("duration", out var value) && value is ScalarParam { Text: var text }
                    && Duration.TryParse(text, out var duration) && duration >= ResolvedTimeout(step, defaults))
                    problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.timeout",
                        "The step's time limit must be longer than the wait, or it ends the wait early.", flow.Id));
            }
        }
    }

    /// <summary>Menu rules that need the other flows (DESIGN §2.5): own flow #38, unknown #40 and duplicate items, counts, times.</summary>
    private static void CheckMenus(IReadOnlyList<Flow> flows, ValueDictionary<StepDefault> defaults, List<Problem> problems)
    {
        var ids = flows.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == ProviderCatalog.MenuShowId))
            {
                var path = $"flows.{flow.Id}.nodes.{step.Id}.with";
                if (step.With.TryGetValue("items", out var value) && value is ListParam { Items: var all } && all.All(i => i is ScalarParam))
                {
                    var items = MenuItems(step);
                    if (items.Count is 0 or > ProviderCatalog.MaxMenuItems)
                        problems.Add(new(Severity.Error, path + ".items", $"A menu lists 1 to {ProviderCatalog.MaxMenuItems} flows.", flow.Id));
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in items)
                    {
                        if (item.Length == 0)
                            problems.Add(new(Severity.Warning, path + ".items", "An empty entry is skipped; pick a flow or remove it.", flow.Id));
                        else if (item == flow.Id)
                            problems.Add(new(Severity.Error, path + ".items", "A menu can't list its own flow.", flow.Id));
                        else if (!ids.Contains(item))
                            problems.Add(new(Severity.Warning, path + ".items", $"{item} isn't there anymore, so the menu leaves it out.", flow.Id));
                        else if (!seen.Add(item))
                            problems.Add(new(Severity.Warning, path + ".items", $"{item} is listed twice; the menu shows it once.", flow.Id));
                    }
                }
                var closeAfter = TimeSpan.FromSeconds(30);
                if (step.With.TryGetValue("closeAfter", out var close) && close is ScalarParam { Text: var text } && Duration.TryParse(text, out var parsed))
                {
                    closeAfter = parsed;
                    if (parsed < ProviderCatalog.MinCloseAfter || parsed > ProviderCatalog.MaxCloseAfter)
                        problems.Add(new(Severity.Error, path + ".closeAfter", "Close after 5s to 10m.", flow.Id));
                }
                if (ResolvedTimeout(step, defaults) <= closeAfter)
                    problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.timeout",
                        "The step's time limit must be longer than \"close after\", or it ends the menu first.", flow.Id));
            }
        }
    }

    /// <summary>The flow a <c>flow.run</c> step names, or null when it names none (that is the param check's error).</summary>
    public static string? CalledFlow(StepNode step) =>
        step.With.TryGetValue("flow", out var value) && value is ScalarParam { Text: var text } ? text.Trim() : null;

    /// <summary>Flows from <paramref name="from"/> to <paramref name="to"/> along calls (from included, to not), or null.</summary>
    private static List<string>? PathBetween(Dictionary<string, HashSet<string>> graph, string from, string to)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal) { [from] = null };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var next in graph[id].Order(StringComparer.Ordinal))
            {
                if (next == to)
                {
                    var path = new List<string>();
                    for (string? at = id; at is not null; at = previous[at])
                        path.Insert(0, at);
                    return path;
                }
                if (previous.TryAdd(next, id))
                    queue.Enqueue(next);
            }
        }
        return null;
    }

    private static string LibraryName(LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => "audio device",
        LibraryKind.Bluetooth => "Bluetooth device",
        LibraryKind.Display => "screen layout",
        LibraryKind.Android => "Android device",
        _ => kind.ToString(),
    };

    /// <summary>"an audio device", "a Bluetooth device".</summary>
    private static string WithArticle(LibraryKind kind) => (kind == LibraryKind.Audio || kind == LibraryKind.Android ? "an " : "a ") + LibraryName(kind);

    /// <summary>Why <paramref name="key"/> can't be a <paramref name="kind"/> from the library, or null.</summary>
    private static string? LibraryKeyProblem(Library library, HashSet<(LibraryKind, string)> badLibraryItems, LibraryKind kind, string key) =>
        !library.Contains(kind, key) ? $"No {LibraryName(kind)} called \"{key}\" in the library."
        : badLibraryItems.Contains((kind, key)) ? $"The {LibraryName(kind)} \"{key}\" needs fixing in the library first."
        : null;

    /// <summary>
    /// What each <c>flow.run</c> passes (D-064): an input of the called flow, given a Library key of its kind or one of
    /// the caller's own inputs (<c>$name</c>) of the same kind. Calls of unknown flows are <see cref="CheckCalls"/>' error.
    /// </summary>
    private static void CheckPassedInputs(IReadOnlyList<Flow> flows, Library library, HashSet<(LibraryKind, string)> badLibraryItems,
        List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == ProviderCatalog.RunFlowId))
            {
                if (CalledFlow(step) is not { } target || flows.FirstOrDefault(f => f.Id == target) is not { } called)
                    continue;
                foreach (var (name, value) in FlowInputs.Given(step))
                {
                    var path = $"flows.{flow.Id}.nodes.{step.Id}.with.{FlowInputs.Param}.{name}";
                    string? message;
                    if (!called.Inputs.TryGetValue(name, out var input))
                        message = $"{called.Name} has no input called \"{name}\".";
                    else if (FlowInputs.NameIn(value) is { } own)
                        message = !flow.Inputs.TryGetValue(own, out var mine) ? $"This flow has no input called \"{own}\"."
                            : mine.Kind != input.Kind ? $"{mine.Label} is {WithArticle(mine.Kind)}; {input.Label} needs {WithArticle(input.Kind)}."
                            : null;
                    else
                        message = LibraryKeyProblem(library, badLibraryItems, input.Kind, value);
                    if (message is not null)
                        problems.Add(new(Severity.Error, path, message, flow.Id));
                }
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex InputName();

    [GeneratedRegex("^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$")]
    private static partial Regex BluetoothAddress();

    [GeneratedRegex("^[0-9A-Fa-f]{4,5}$")]
    private static partial Regex GlyphCode();

    private sealed class FlowChecker(Flow flow, Library library, HashSet<(LibraryKind, string)> badLibraryItems,
        IReadOnlyList<ProviderDescriptor> catalog, List<Problem> problems)
    {
        private readonly string _path = $"flows.{flow.Id}";
        private readonly Dictionary<string, Node> _nodes = [];
        private readonly Dictionary<string, string> _cardOf = []; // member id → card id

        public void Check()
        {
            CheckSettings();
            if (flow.Nodes.Count == 0)
                Add(Severity.Warning, _path + ".nodes", "This flow has no steps yet.");
            foreach (var node in flow.Nodes)
            {
                if (!_nodes.TryAdd(node.Id, node))
                    Error(NodePath(node), $"Another step or card is already called \"{node.Id}\".");
            }
            foreach (var card in flow.Nodes.OfType<GroupNode>())
                CheckCard(card);
            foreach (var node in flow.Nodes)
            {
                CheckEdges(node);
                if (node is StepNode step)
                    CheckStep(step);
            }
            CheckCycles();
        }

        private void CheckSettings()
        {
            if (flow.Glyph is { } glyph && !GlyphCode().IsMatch(glyph))
                Add(Severity.Warning, _path + ".glyph", $"\"{glyph}\" isn't a Segoe Fluent Icons code (like E702); the step's icon is used.");
            foreach (var (name, input) in flow.Inputs)
            {
                var path = $"{_path}.inputs.{name}";
                if (!InputName().IsMatch(name))
                    Error(path, "Name inputs with small letters, digits and dashes, starting with a letter (like output).");
                if (input.Default.Length == 0)
                    Error(path + ".default", $"{input.Label} needs a default device.");
                else if (LibraryKeyProblem(library, badLibraryItems, input.Kind, input.Default) is { } message)
                    Error(path + ".default", message);
            }
            foreach (var text in flow.Hotkeys)
            {
                if (!Hotkey.TryParse(text, out _))
                    Error(_path + ".hotkeys", $"\"{text}\" isn't a shortcut: use Win, Ctrl or Alt plus a letter, number, F1–F24 or a key like [, or a number pad key like Num1.");
            }
        }

        /// <summary>
        /// Only enabled flows without errors hold hotkeys; the later of two clashing flows gets the error. All or
        /// nothing: a flow that fails here claims none of its hotkeys, so it can't block a third flow.
        /// </summary>
        public void ClaimHotkeys(Dictionary<Hotkey, string> hotkeyOwners, IReadOnlySet<string> rejected)
        {
            if (!flow.Enabled || rejected.Contains(flow.Id))
                return;
            var mine = new HashSet<Hotkey>();
            var failed = false;
            foreach (var text in flow.Hotkeys)
            {
                if (!Hotkey.TryParse(text, out var hotkey))
                    continue;
                var message = !mine.Add(hotkey) ? $"{hotkey} is listed twice."
                    : hotkeyOwners.TryGetValue(hotkey, out var owner) ? $"{hotkey} already runs flow \"{owner}\"."
                    : null;
                if (message is not null)
                {
                    Error(_path + ".hotkeys", message);
                    failed = true;
                }
            }
            if (!failed)
            {
                foreach (var hotkey in mine)
                    hotkeyOwners[hotkey] = flow.Id;
            }
        }

        private void CheckCard(GroupNode card)
        {
            var path = NodePath(card);
            if (card.Members.Count < 2)
                Error(path + ".group", "A card holds at least two steps; use a plain step instead.");
            foreach (var id in card.Members)
            {
                if (!_nodes.TryGetValue(id, out var member))
                    Error(path + ".group", $"No step called \"{id}\" in this flow.");
                else if (member is GroupNode)
                    Error(path + ".group", $"\"{id}\" is a card; cards can't hold cards.");
                else if (!_cardOf.TryAdd(id, card.Id))
                    Error(path + ".group", _cardOf[id] == card.Id ? $"\"{id}\" is listed twice." : $"\"{id}\" is already in card \"{_cardOf[id]}\".");
                else if (member.After.Count > 0)
                    Error(NodePath(member) + ".after", $"\"{id}\" is in card \"{card.Id}\"; connect the card instead.");
            }
        }

        private void CheckEdges(Node node)
        {
            var path = NodePath(node) + ".after";
            var seen = new HashSet<string>();
            foreach (var edge in node.After)
            {
                if (edge.From == node.Id)
                    Error(path, "A step can't wait for itself.");
                else if (!_nodes.ContainsKey(edge.From))
                    Error(path, $"No step or card called \"{edge.From}\" in this flow.");
                else if (_cardOf.TryGetValue(edge.From, out var card))
                    Error(path, $"\"{edge.From}\" is in card \"{card}\"; wait for the card instead.");
                else if (!seen.Add(edge.From))
                    Error(path, $"\"{edge.From}\" is listed twice.");
            }
        }

        private void CheckStep(StepNode step)
        {
            var path = NodePath(step);
            if (step.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > MaxDuration))
                Error(path + ".timeout", "Give the step more than 0 s and at most 24h.");
            if (step.RetryCount is < 0 or > MaxRetries)
                Error(path + ".retry.count", $"Try again 0 to {MaxRetries} times.");
            if (step.RetryDelay is { } wait && (wait < TimeSpan.Zero || wait > MaxDuration))
                Error(path + ".retry.delay", "Wait 0 s to 24h between tries.");
            var descriptor = catalog.FirstOrDefault(d => d.Id == step.Step);
            if (descriptor is null)
            {
                Error(path + ".step", step.Step.StartsWith("firetv.", StringComparison.Ordinal)
                    ? $"\"{step.Step}\" is now called \"android.{step.Step["firetv.".Length..]}\"." // clean rename (D-045)
                    : $"\"{step.Step}\" isn't a known step type.");
                return;
            }
            foreach (var (name, value) in step.With)
            {
                if (descriptor.FindParam(name) is { } spec)
                    CheckParam(spec, value, $"{path}.with.{name}", passThrough: descriptor.Id == ProviderCatalog.RunFlowId && name == FlowInputs.Param);
                else
                    Error($"{path}.with.{name}", $"{descriptor.Title} has no setting \"{name}\" ({Names(descriptor)}).");
            }
            foreach (var spec in descriptor.Params.Where(p => p.Required && !step.With.ContainsKey(p.Name)))
                Error(path + ".with", $"{spec.Title} is missing ({spec.Name}: …).");
        }

        /// <summary>This flow's <c>$name</c> inside a list or map: only device settings take inputs (they aren't filled in there).</summary>
        private void CheckNoInputs(IEnumerable<ParamValue> values, string path)
        {
            if (values.Select(FlowInputs.NameIn).FirstOrDefault(n => n is not null && flow.Inputs.ContainsKey(n)) is { } name)
                Error(path, $"Inputs (${name}) only go in device settings.");
        }

        private static string Names(ProviderDescriptor descriptor) =>
            descriptor.Params.Count == 0 ? "it takes none" : "it takes " + string.Join(", ", descriptor.Params.Select(p => p.Name));

        /// <param name="passThrough"><c>flow.run</c>'s <c>inputs</c>: its values may be this flow's <c>$name</c> (checked by CheckPassedInputs).</param>
        private void CheckParam(ParamSpec spec, ParamValue value, string path, bool passThrough = false)
        {
            switch (spec.Kind)
            {
                case ParamKind.TextList:
                    if (value is not ListParam list)
                        Error(path, "Write a list: [a, b].");
                    else if (list.Items.Any(i => i is not ScalarParam))
                        Error(path, "List items must be plain values.");
                    else
                        CheckNoInputs(list.Items, path);
                    return;
                case ParamKind.TextMap:
                    if (value is not MapParam map)
                        Error(path, "Write name: value pairs: { Accept: text/plain }.");
                    else if (map.Entries.Values.Any(v => v is not ScalarParam))
                        Error(path, "Values must be plain values.");
                    else if (!passThrough)
                        CheckNoInputs(map.Entries.Values, path);
                    return;
            }
            if (value is not ScalarParam { Text: var text })
            {
                Error(path, "Write a single value here, not a list or mapping.");
                return;
            }
            if (spec.Required && string.IsNullOrWhiteSpace(text))
            {
                Error(path, $"{spec.Title} can't be empty.");
                return;
            }
            if (spec.Kind == ParamKind.LibraryRef && FlowInputs.NameIn(value) is { } name)
            {
                var kind = spec.Library!.Value;
                if (!flow.Inputs.TryGetValue(name, out var input))
                    Error(path, $"This flow has no input called \"{name}\".");
                else if (input.Kind != kind)
                    Error(path, $"{input.Label} is {WithArticle(input.Kind)}; this setting needs {WithArticle(kind)}.");
                return;
            }
            // Only this flow's own input names count, so text that merely starts with $ ("$5 off") stays plain text.
            if (FlowInputs.NameIn(value) is { } misplaced && flow.Inputs.ContainsKey(misplaced))
            {
                Error(path, $"Inputs (${misplaced}) only go in device settings.");
                return;
            }
            var message = spec.Kind switch
            {
                ParamKind.WholeNumber when !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) =>
                    $"\"{text}\" isn't a whole number.",
                ParamKind.WholeNumber when int.Parse(text, CultureInfo.InvariantCulture) is var n && (n < spec.Min || n > spec.Max) =>
                    $"Use {spec.Min} to {spec.Max}.",
                ParamKind.Boolean when !bool.TryParse(text, out _) => $"\"{text}\" isn't true or false.",
                ParamKind.Duration when !Duration.TryParse(text, out _) => $"\"{text}\" isn't a duration (like 500ms, 20s, 1m30s).",
                ParamKind.Choice when !spec.Choices.Contains(text) => $"\"{text}\" isn't one of: {string.Join(", ", spec.Choices)}.",
                ParamKind.LibraryRef when text.Length > 0 => LibraryKeyProblem(library, badLibraryItems, spec.Library!.Value, text),
                _ => null,
            };
            if (message is not null)
                Error(path, message);
        }

        /// <summary>Reports each loop once, at the node where the search first closed it.</summary>
        private void CheckCycles()
        {
            var state = new Dictionary<string, bool>(); // false = on the current path, true = done
            var path = new List<string>();
            foreach (var node in flow.Nodes)
                Visit(node.Id);

            void Visit(string id)
            {
                if (state.TryGetValue(id, out var done))
                {
                    if (!done)
                    {
                        var loop = path[path.IndexOf(id)..].Append(id);
                        Error(NodePath(_nodes[id]) + ".after", $"Steps wait for each other in a loop: {string.Join(" → ", loop)}.");
                    }
                    return;
                }
                state[id] = false;
                path.Add(id);
                foreach (var edge in _nodes[id].After.Where(e => _nodes.ContainsKey(e.From) && e.From != id))
                    Visit(edge.From);
                path.RemoveAt(path.Count - 1);
                state[id] = true;
            }
        }

        private string NodePath(Node node) => $"{_path}.nodes.{node.Id}";

        private void Error(string path, string message) => Add(Severity.Error, path, message);

        private void Add(Severity severity, string path, string message) => problems.Add(new(severity, path, message, flow.Id));
    }
}
