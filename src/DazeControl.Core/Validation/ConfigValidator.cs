using System.Globalization;
using System.Text.RegularExpressions;
using DazeControl.Core.Catalog;
using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;

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
    /// <param name="folderExists">Whether a watched folder is there (<see cref="Directory.Exists"/> by default); tests pass their own.</param>
    public static ValueList<Problem> Validate(Configuration config, IEnumerable<Problem>? earlier = null,
        IReadOnlyList<ProviderDescriptor>? catalog = null, Func<string, bool>? folderExists = null)
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
        CheckBound(config.Library, badLibraryItems, problems);
        Flow? Calls(string id) => config.Flows.FirstOrDefault(f => f.Id == id);
        var checkers = config.Flows.Select(f => new FlowChecker(f, config.Library, badLibraryItems, catalog, config.Settings.StepDefaults, problems, Calls)).ToList();
        foreach (var checker in checkers)
            checker.Check();
        CheckMenus(config.Flows, config.Settings.StepDefaults, problems);
        CheckPrompts(config.Flows, config.Settings.StepDefaults, problems);
        CheckPicks(config.Flows, config.Library, config.Settings.StepDefaults, problems);
        CheckTimeWindows(config.Flows, problems);
        CheckWaits(config.Flows, config.Settings.StepDefaults, problems);
        CheckStops(config.Flows, problems);
        CheckOpens(config.Flows, problems);
        CheckValues(config.Flows, problems);
        CheckWakes(config.Flows, problems);
        CheckExtracts(config.Flows, problems);
        CheckReaches(config.Flows, problems);
        CheckCalls(config.Flows, problems);
        CheckWatchedFolders(config.Flows, folderExists ?? Directory.Exists, problems);
        CheckPassedInputs(config.Flows, config.Library, badLibraryItems, catalog, config.Settings.StepDefaults, problems);
        CheckOnlyIf(config, catalog, folderExists, problems);
        // Second pass, so a flow that is rejected anyway (or turned off) can't take a hotkey from a healthy one.
        var hotkeyOwners = new Dictionary<Hotkey, string>();
        var rejected = FlowsWithErrors(problems.Concat(earlier ?? []));
        foreach (var checker in checkers)
            checker.ClaimHotkeys(hotkeyOwners, rejected);
        return new(problems);
    }

    /// <summary>
    /// "Only run if" (T25.1, D-150): each entry must be a check step (<see cref="ProviderCatalog.Checks"/>). The checks are
    /// validated by every step rule above, as a flow of their own with the flow's inputs (that is how the guard runs them), and
    /// the problems are moved to <c>flows.&lt;id&gt;.onlyIf.&lt;check&gt;</c>. A problem there is a flow error, as for a step:
    /// a broken guard must not let a flow start unguarded.
    /// </summary>
    private static void CheckOnlyIf(Configuration config, IReadOnlyList<ProviderDescriptor> catalog, Func<string, bool>? folderExists, List<Problem> problems)
    {
        var guarded = config.Flows.Where(f => f.OnlyIf.Count > 0).ToList();
        if (guarded.Count == 0)
            return;
        foreach (var flow in guarded)
        {
            foreach (var check in flow.OnlyIf.Where(c => !ProviderCatalog.Checks.Contains(c.Step) && catalog.Any(d => d.Id == c.Step)))
                problems.Add(new(Severity.Error, $"flows.{flow.Id}.onlyIf.{check.Id}.step",
                    $"\"Only run if\" takes checks only; {catalog.First(d => d.Id == check.Step).Title} changes something.", flow.Id));
        }
        var checks = guarded.Select(f => GuardFlow(f)).ToList();
        var found = Validate(config with { Flows = new(checks) }, catalog: catalog, folderExists: folderExists);
        foreach (var problem in found)
        {
            if (problem.FlowId is not { } id)
                continue;
            var nodes = $"flows.{id}.nodes";
            if (!problem.Path.StartsWith(nodes + ".", StringComparison.Ordinal))
                continue; // the stand-in flow's own warnings (it has no hotkeys, triggers or outputs) aren't the flow's
            problems.Add(problem with { Path = $"flows.{id}.onlyIf" + problem.Path[nodes.Length..] });
        }
    }

    /// <summary>
    /// The flow the guard runs (T25.1): the "Only run if" checks side by side, with the flow's inputs and nothing else. Each
    /// is "skip what depends on it", so one check's failure doesn't cancel the others: a "no" from any of them must be seen
    /// even when another one couldn't answer.
    /// </summary>
    public static Flow GuardFlow(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return new Flow(flow.Id, flow.Name, new(flow.OnlyIf.Select(c => (Node)(c with { After = [], OnFailure = FailurePolicy.Skip, Only = null }))))
        {
            Inputs = flow.Inputs,
            Popup = false,
        };
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

    /// <summary>Stop flow (T24.1, D-133) ends the flow, so a step linked after it never runs: a warning, not an error (the link is harmless).</summary>
    /// <summary>
    /// A watched folder that isn't there is a plain warning (T26.5): the trigger stays and starts working when the folder appears.
    /// A network share is not asked (a dead one can stall the editor for seconds); the engine logs it when it can't watch it.
    /// </summary>
    private static void CheckWatchedFolders(IReadOnlyList<Flow> flows, Func<string, bool> folderExists, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            for (var i = 0; i < flow.Triggers.Count; i++)
            {
                if (flow.Triggers[i] is not EventTrigger e || !TriggerText.IsFileEvent(e.Name) || TriggerText.ResolveFolder(e.Path, out _) is not { } folder
                    || folder.StartsWith(@"\\", StringComparison.Ordinal) || folderExists(folder))
                    continue;
                problems.Add(new(Severity.Warning, $"flows.{flow.Id}.triggers[{i}].path",
                    $"The folder {folder} isn't there. This trigger starts working once it is.", flow.Id));
            }
        }
    }

    private static void CheckStops(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            var stopSteps = flow.Nodes.OfType<StepNode>().Where(s => s.Step == ProviderCatalog.StopFlowId && s.Enabled).ToList(); // one turned off ends nothing (D-148)
            if (stopSteps.Count == 0)
                continue;
            // A stop in a parallel card: links leave from the card, and the card never finishes (members have no edges, D-003).
            var stops = stopSteps.Select(s => s.Id).Concat(flow.Nodes.OfType<GroupNode>().Where(g => g.Members.Any(m => stopSteps.Any(s => s.Id == m))).Select(g => g.Id))
                .ToHashSet(StringComparer.Ordinal);
            // Only "As failed" fires a failure link: that is the way to react to it, so it isn't warned about.
            var failing = stopSteps.Where(s => s.With.TryGetValue("mode", out var m) && m is ScalarParam { Text: "failed" }).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var node in flow.Nodes.Where(n => n.After.Any(e => stops.Contains(e.From) && !(e.When == EdgeWhen.Failure && failing.Contains(e.From)))))
                problems.Add(new(Severity.Warning, $"flows.{flow.Id}.nodes.{node.Id}.after",
                    "A step after Stop flow never runs: it ends the flow. Remove the link, or put Stop flow last.", flow.Id));
        }
    }

    /// <summary>A written target that <c>process.open</c> would always refuse is an error now, not a failed step later (D-134).</summary>
    private static void CheckOpens(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == "process.open"))
            {
                if (!step.With.TryGetValue("target", out var setting) || setting is not ScalarParam { Text: var text })
                    continue;
                var written = text.Trim(' ');
                var scheme = StepValues.SchemeOf(written);
                var path = $"flows.{flow.Id}.nodes.{step.Id}.with.target";
                if (scheme is not null && StepValues.RefusedSchemes.Contains(scheme))
                    problems.Add(new(Severity.Error, path, $"Open file or address doesn't open {scheme}: addresses.", flow.Id));
                else if (scheme is not null && written.Length == scheme.Length + 1)
                    problems.Add(new(Severity.Error, path, $"Add what to open after \"{scheme}:\".", flow.Id));
                else if (!written.Contains('{', StringComparison.Ordinal) && text.Any(c => char.IsControl(c) || c == '"'))
                    problems.Add(new(Severity.Error, path, "What to open can't hold quotes or line breaks. It is one file, folder or address, not a command.", flow.Id));
            }
        }
    }

    /// <summary>A written comparison <c>value.check</c> would always refuse is an error now, not a failed step later (D-137). Text with a reference is unknown until the run.</summary>
    private static void CheckValues(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == ProviderCatalog.ValueCheckId))
            {
                var op = step.With.TryGetValue("is", out var o) && o is ScalarParam { Text: var chosen } ? chosen.Trim() : "is";
                string? Written(string name) => step.With.TryGetValue(name, out var p) && p is ScalarParam { Text: var t } ? t.Trim() : "";
                var path = $"flows.{flow.Id}.nodes.{step.Id}.with";
                if (op is "contains" or "doesNotContain" or "startsWith" or "endsWith" && Written("to")!.Length == 0)
                    problems.Add(new(Severity.Error, $"{path}.to", "Fill in what to compare with: this comparison needs something to look for.", flow.Id));
                else if (op is "atLeast" or "below")
                {
                    foreach (var side in StepValues.CheckedSides)
                    {
                        var text = Written(side)!;
                        if (StepValues.Parse(text).Count == 0 && !StepValues.TryNumber(text, out _))
                            problems.Add(new(Severity.Error, $"{path}.{side}", $"\"{text}\" isn't a number. At least and below compare numbers, like 3 or 2.5.", flow.Id));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Get from text (D-144): a written field path, pattern or line the step would always refuse is an error now. It runs the same
    /// <see cref="TextExtract.Check"/> as the step, on the text as the step will see it (escapes undone). A setting holding a
    /// reference is unknown until the run, so then only the choice of what to take is known.
    /// </summary>
    private static void CheckExtracts(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == ProviderCatalog.ExtractId))
            {
                string? Written(string name) => step.With.TryGetValue(name, out var p) && p is ScalarParam { Text: var t } ? t : null;
                // A called flow's answer is cut at 1,000 characters before this step could read it (D-144): refuse it as the text.
                if (StepValues.Parse(Written("text")).Any(r => r.Source == ValueSource.Step && flow.FindNode(r.Name) is StepNode { } called && FlowOutputs.Waits(called)))
                    problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.with.text", "A called flow's answer is cut at 1,000 characters, so Get from text can't read it. Take the part inside that flow instead.", flow.Id));
                var by = (Written("by") ?? "json").Trim();
                var find = Written("find") ?? "";
                var until = Written("until") ?? "";
                if (!TextExtract.Modes.Contains(by) || StepValues.Parse(find).Count > 0 || StepValues.Parse(until).Count > 0)
                    continue;
                if (TextExtract.Check(by, TextExtract.AsFilled(find), TextExtract.AsFilled(until)) is { } why)
                    problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.with.find", why, flow.Id));
            }
        }
    }

    /// <summary>A written hardware address <c>network.wake</c> would always refuse is an error now (D-139). Text holding a reference is unknown until the run.</summary>
    private static void CheckWakes(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == "network.wake"))
            {
                if (!step.With.TryGetValue("mac", out var setting) || setting is not ScalarParam { Text: var text } || text.Trim().Length == 0 || StepValues.Parse(text).Count > 0)
                    continue;
                if (!MacAddress.TryParse(text, out _))
                    problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.with.mac",
                        "That isn't a hardware address. Use six pairs of digits, like aa:bb:cc:dd:ee:ff.", flow.Id));
            }
        }
    }

    /// <summary>Check address (D-140): a written address, answer range or wait it would always refuse is an error now. Text holding a reference is unknown until the run.</summary>
    private static void CheckReaches(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == ProviderCatalog.ReachId))
            {
                var path = $"flows.{flow.Id}.nodes.{step.Id}.with";
                if (step.With.TryGetValue("address", out var address) && address is ScalarParam { Text: var text } && text.Trim().Length > 0 && StepValues.Parse(text).Count == 0
                    && !ReachAddress.TryParse(text, out _, out var why))
                    problems.Add(new(Severity.Error, $"{path}.address", why, flow.Id));
                if (step.With.TryGetValue("status", out var status) && status is ScalarParam { Text: var codes } && ReachAddress.TryStatuses(codes) is null)
                    problems.Add(new(Severity.Error, $"{path}.status", "Use answer codes like 200, 200-299 or 200-299, 401 (100 to 599).", flow.Id));
                if (step.With.TryGetValue("timeout", out var wait) && wait is ScalarParam { Text: var span } && Duration.TryParse(span, out var parsed)
                    && (parsed < ReachAddress.MinTimeout || parsed > ReachAddress.MaxTimeout))
                    problems.Add(new(Severity.Error, $"{path}.timeout", "Use 1 s to 20 s.", flow.Id));
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

    /// <summary><c>time.check</c> (D-118): From and To are times of day, both given or both empty, and not the same.</summary>
    private static void CheckTimeWindows(IReadOnlyList<Flow> flows, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == "time.check"))
            {
                var path = $"flows.{flow.Id}.nodes.{step.Id}.with";
                string? Text(string name) => step.With.TryGetValue(name, out var v) && v is ScalarParam s && s.Text.Trim() is { Length: > 0 } t ? t : null;
                TimeOnly? Time(string name)
                {
                    if (Text(name) is not { } text)
                        return null;
                    if (TriggerText.TryParseTime(text, out var time))
                        return time;
                    problems.Add(new(Severity.Error, $"{path}.{name}", $"\"{text}\" isn't a time of day (like 07:00 or 22:30).", flow.Id));
                    return null;
                }
                var (from, to) = (Time("from"), Time("to"));
                if ((Text("from") is null) != (Text("to") is null))
                    problems.Add(new(Severity.Error, $"{path}.{(Text("from") is null ? "from" : "to")}", "Give both From and To, or neither for all day.", flow.Id));
                else if (from is not null && from == to)
                    problems.Add(new(Severity.Error, $"{path}.to", "To must differ from From.", flow.Id));
            }
        }
    }

    /// <summary><c>prompt.ask</c> rules that read several settings (D-107): answers, the default, the wait against the step's time limit.</summary>
    private static void CheckPrompts(IReadOnlyList<Flow> flows, ValueDictionary<StepDefault> defaults, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(s => s.Step == ProviderCatalog.PromptAskId))
            {
                var path = $"flows.{flow.Id}.nodes.{step.Id}.with";
                string? Text(string name) => step.With.TryGetValue(name, out var v) && v is ScalarParam s ? s.Text.Trim() : null;
                void Bad(string name, string message) => problems.Add(new(Severity.Error, $"{path}.{name}", message, flow.Id));
                var choose = Text("mode") == Prompts.Choose;
                List<string> answers = step.With.TryGetValue("choices", out var list) && list is ListParam { Items: var items }
                    ? [.. items.OfType<ScalarParam>().Select(i => i.Text.Trim())]
                    : [];
                if (choose)
                {
                    if (answers.Count is < 2 or > Prompts.MaxChoices)
                        Bad("choices", $"Choose needs 2 to {Prompts.MaxChoices} answers.");
                    else if (answers.Any(a => a.Length == 0))
                        Bad("choices", "An answer can't be empty.");
                    else if (answers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != answers.Count)
                        Bad("choices", "Two answers are the same.");
                }
                else if (answers.Count > 0)
                {
                    problems.Add(new(Severity.Warning, $"{path}.choices", "Yes or No has no answers to list; they are ignored.", flow.Id));
                }
                var onTimeout = Text("onTimeout") ?? "";
                var fallback = Text("default");
                if (onTimeout == Prompts.UseDefault)
                {
                    if (string.IsNullOrEmpty(fallback))
                        Bad("default", "Give the default answer to use when nobody answers.");
                    else if (!choose && !fallback.Equals("yes", StringComparison.OrdinalIgnoreCase) && !fallback.Equals("no", StringComparison.OrdinalIgnoreCase))
                        Bad("default", "The default answer is yes or no.");
                }
                if (choose && !string.IsNullOrEmpty(fallback) && !answers.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                    Bad("default", $"\"{fallback}\" isn't one of the answers.");
                if (!choose && onTimeout == Prompts.Skip && Text("flag") is { Length: > 0 })
                    problems.Add(new(Severity.Warning, $"{path}.onTimeout", "Carrying on without an answer leaves the flag as it was.", flow.Id));
                var wait = Prompts.DefaultTimeout;
                if (Text("timeout") is { } text && Duration.TryParse(text, out var parsed))
                {
                    wait = parsed;
                    if (parsed < Prompts.MinTimeout || parsed > Prompts.MaxTimeout)
                        Bad("timeout", "Wait 5s to 10m for an answer.");
                }
                if (ResolvedTimeout(step, defaults) <= wait)
                    problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.timeout",
                        "The step's time limit must be longer than the wait, or it ends the question first.", flow.Id));
            }
        }
    }

    /// <summary><c>prompt.pick</c> rules (D-117): a known kind, a default from the Library, the wait against the step's time limit.</summary>
    private static void CheckPicks(IReadOnlyList<Flow> flows, Library library, ValueDictionary<StepDefault> defaults, List<Problem> problems)
    {
        foreach (var flow in flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>().Where(PickResults.IsPick))
            {
                var path = $"flows.{flow.Id}.nodes.{step.Id}.with";
                string? Text(string name) => step.With.TryGetValue(name, out var v) && v is ScalarParam s ? s.Text.Trim() : null;
                void Bad(string name, string message) => problems.Add(new(Severity.Error, $"{path}.{name}", message, flow.Id));
                var fallback = Text("default");
                if (PickResults.KindOf(step) is { } kind)
                {
                    // A key, or (older flows) a name that picks out one item; an audio or Bluetooth pick also offers bound devices (D-116, D-128).
                    // Only a warning: the pick still works, a missing default just ends the wait with "No answer" (D-021).
                    if (!string.IsNullOrEmpty(fallback) && PickResults.DefaultKey(library, kind, fallback) is null)
                        problems.Add(new(Severity.Warning, $"{path}.default", Library.Missing(kind, fallback), flow.Id));
                }
                if (Text("onTimeout") == Prompts.UseDefault && string.IsNullOrEmpty(fallback))
                    Bad("default", "Give the default to use when nobody answers.");
                var wait = Prompts.DefaultTimeout;
                if (Text("timeout") is { } text && Duration.TryParse(text, out var parsed))
                {
                    wait = parsed;
                    if (parsed < Prompts.MinTimeout || parsed > Prompts.MaxTimeout)
                        Bad("timeout", "Wait 5s to 10m for an answer.");
                }
                if (ResolvedTimeout(step, defaults) <= wait)
                    problems.Add(new(Severity.Error, $"flows.{flow.Id}.nodes.{step.Id}.timeout",
                        "The step's time limit must be longer than the wait, or it ends the question first.", flow.Id));
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

    /// <summary>
    /// Bound devices (D-116): a key a Bluetooth or audio device also has is an error (a step couldn't tell which it
    /// means); a missing side is only a warning, so steps using the other side still run (fail-soft, D-021).
    /// </summary>
    private static void CheckBound(Library library, HashSet<(LibraryKind, string)> badLibraryItems, List<Problem> problems)
    {
        foreach (var bound in library.Bound)
        {
            var path = $"library.bound.{bound.Key}";
            if (LibraryEdits.KeyClashes(library, LibraryKind.Bound, bound.Key))
            {
                badLibraryItems.Add((LibraryKind.Bound, bound.Key));
                problems.Add(new(Severity.Error, path, $"\"{bound.Key}\" is also the name of a device in your Library; give the bound device its own name."));
                continue;
            }
            if (!library.Bluetooth.Any(d => d.Key == bound.Bluetooth))
                problems.Add(new(Severity.Warning, path + ".bluetooth", Library.Missing(LibraryKind.Bluetooth, bound.Bluetooth)));
            else if (badLibraryItems.Contains((LibraryKind.Bluetooth, bound.Bluetooth)))
                badLibraryItems.Add((LibraryKind.Bluetooth, bound.Key));
            if (!library.Audio.Any(d => d.Key == bound.Audio))
                problems.Add(new(Severity.Warning, path + ".audio", Library.Missing(LibraryKind.Audio, bound.Audio)));
        }
    }

    private static string LibraryName(LibraryKind kind) => Library.KindName(kind);

    /// <summary>"an audio device", "a Bluetooth device".</summary>
    private static string WithArticle(LibraryKind kind) => (kind == LibraryKind.Audio || kind == LibraryKind.Android ? "an " : "a ") + LibraryName(kind);

    /// <summary>Why <paramref name="key"/> can't be a <paramref name="kind"/> from the library, or null.</summary>
    private static string? LibraryKeyProblem(Library library, HashSet<(LibraryKind, string)> badLibraryItems, LibraryKind kind, string key) =>
        library.Bound.Any(b => b.Key == key) && kind is LibraryKind.Audio or LibraryKind.Bluetooth && !library.Contains(kind, key)
            ? $"The bound device \"{key}\" has no {LibraryName(kind)} in your Library."
        : !library.Contains(kind, key) ? Library.Missing(kind, key)
        : badLibraryItems.Contains((kind, key)) || badLibraryItems.Contains((LibraryKind.Bound, key))
            ? $"The {LibraryName(kind)} \"{key}\" needs fixing in your Library first."
        : null;

    /// <summary>
    /// What each <c>flow.run</c> passes (D-064): an input of the called flow, given a Library key of its kind or one of
    /// the caller's own inputs (<c>$name</c>) of the same kind. Calls of unknown flows are <see cref="CheckCalls"/>' error.
    /// </summary>
    private static void CheckPassedInputs(IReadOnlyList<Flow> flows, Library library, HashSet<(LibraryKind, string)> badLibraryItems,
        IReadOnlyList<ProviderDescriptor> catalog, ValueDictionary<StepDefault> defaults, List<Problem> problems)
    {
        Flow? Calls(string id) => flows.FirstOrDefault(f => f.Id == id);
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
                    else if (input.Kind is not { } kind)
                    {
                        // A text input (T23.7): text that takes values, filled in this flow; or this flow's own text input.
                        message = FlowInputs.NameIn(value) is { } ownText && flow.Inputs.TryGetValue(ownText, out var mineText) && mineText.Kind is { } mineKind
                            ? $"{mineText.Label} is {WithArticle(mineKind)}; {input.Label} needs text."
                            : value.Length > RunValues.MaxValueLength ? $"{input.Label} holds at most {RunValues.MaxValueLength} characters."
                            : null;
                        if (message is null)
                        {
                            foreach (var problem in StepValues.Problems(flow, step.Id, FlowInputs.TextSpec(input.Label), value, catalog, defaults, Calls))
                                problems.Add(new(problem.Severity, path, problem.Message, flow.Id));
                        }
                    }
                    else if (PickResults.StepIn(value) is not null)
                        message = PickResults.ResultProblem(flow, step.Id, value, kind);
                    else if (FlowInputs.NameIn(value) is { } own)
                        message = !flow.Inputs.TryGetValue(own, out var mine) ? $"This flow has no input called \"{own}\"."
                            : mine.Kind is not { } myKind ? $"{mine.Label} is text; {input.Label} needs {WithArticle(kind)}."
                            : !LibraryEdits.Refers(myKind, kind) ? $"{mine.Label} is {WithArticle(myKind)}; {input.Label} needs {WithArticle(kind)}."
                            : null;
                    else
                        message = LibraryKeyProblem(library, badLibraryItems, kind, value);
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

    /// <param name="calls">Finds a called flow by id, for the outputs of <c>flow.run</c> steps (T23.7).</param>
    private sealed class FlowChecker(Flow flow, Library library, HashSet<(LibraryKind, string)> badLibraryItems,
        IReadOnlyList<ProviderDescriptor> catalog, ValueDictionary<StepDefault> defaults, List<Problem> problems, Func<string, Flow?> calls)
    {
        private readonly string _path = $"flows.{flow.Id}";
        private readonly Dictionary<string, Node> _nodes = [];
        private string _step = ""; // the step whose settings CheckParam is reading
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
                if (input.Kind is not { } kind)
                {
                    // A text input (T23.7): any default, even empty; asking needs typing, which takes focus (D-107).
                    if (input.Default.Length > RunValues.MaxValueLength)
                        Error(path + ".default", $"{input.Label}: a text input holds at most {RunValues.MaxValueLength} characters.");
                    if (input.Ask)
                        Error(path + ".ask", $"{input.Label}: text can't be asked for before a run yet; the default is used.");
                }
                else if (input.Default.Length == 0)
                    Error(path + ".default", kind == LibraryKind.Bound
                        ? $"{input.Label}: choose a bound device."
                        : $"{input.Label} needs a default device.");
                else if (LibraryKeyProblem(library, badLibraryItems, kind, input.Default) is { } message)
                    Error(path + ".default", message);
            }
            CheckOutputs();
            CheckTriggers();
            CheckAsks();
            foreach (var text in flow.Hotkeys)
            {
                if (!Hotkey.TryParse(text, out _))
                    Error(_path + ".hotkeys", $"\"{text}\" isn't a shortcut: use Win, Ctrl or Alt plus a letter, number, F1–F24 or a key like [, or a number pad key like Num1.");
            }
        }

        /// <summary>
        /// Triggers a file can't hold (the reader already drops those) but an edited model can. Warnings, so the flow stays (D-021):
        /// the runtime skips a trigger that has one.
        /// </summary>
        private void CheckTriggers()
        {
            for (var i = 0; i < flow.Triggers.Count; i++)
            {
                var path = $"{_path}.triggers[{i}]";
                var trigger = flow.Triggers[i];
                var why = trigger switch
                {
                    EventTrigger e when !TriggerText.IsEvent(e.Name) => $"\"{e.Name}\" isn't an event I know.",
                    EventTrigger e when TriggerText.NetworkFilterProblem(e.Name, e.Via, e.Ssid) is { } filter => filter,
                    EventTrigger e when TriggerText.DeviceFilterProblem(e.Name, e.Device) is { } deviceProblem => deviceProblem,
                    EventTrigger e when TriggerText.FlowFilterProblem(e.Name, e.Flow, flow.Id) is { } flowProblem => flowProblem,
                    EventTrigger e when TriggerText.FileFilterProblem(e.Name, e.Path, e.Filter) is { } fileProblem => fileProblem,
                    EventTrigger e when TriggerText.FlowFilter(e.Flow) is { } id && calls(id) is null =>
                        $"{id} isn't there anymore. Pick another flow.",
                    EventTrigger { Device: { } sound } e when TriggerText.IsAudioEvent(e.Name) && !library.Contains(LibraryKind.Audio, sound) =>
                        Library.Missing(LibraryKind.Audio, sound),
                    EventTrigger { Via: { } via } when !Enum.IsDefined(via) => "via is wifi or cable.",
                    AtTrigger a when a.Days == Days.None => "Pick at least one day.",
                    EveryTrigger e when e.Interval < EveryTrigger.MinInterval || e.Interval > EveryTrigger.MaxInterval => "An interval is from a minute to 24 hours.",
                    ScheduleTrigger { Only: not null } => "only goes with an event, not a schedule.",
                    EveryTrigger e when (e.From is null) != (e.To is null) || (e.From is not null && e.From == e.To) => "between needs two different times.",
                    ScheduleTrigger { Grace: { } g } when g < TimeSpan.Zero => "grace can't be negative.",
                    { Only: { } only } when !string.IsNullOrWhiteSpace(only) && !TriggerText.IsCondition(only) =>
                        $"only \"{only}\" isn't a condition I know ({string.Join(", ", TriggerText.Conditions)}).",
                    _ => null,
                };
                if (why is not null)
                    Add(Severity.Warning, path, why + " This trigger is ignored.");
                else if (trigger.Only is { } only && string.IsNullOrWhiteSpace(only))
                    Add(Severity.Warning, path + ".only", "only is empty.");
                else if (flow.Triggers.Take(i).Contains(trigger))
                    Add(Severity.Warning, path, "The same trigger is already listed; it would fire twice.");
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

        /// <summary>The flow's outputs (T23.7): at most <see cref="FlowOutputs.MaxCount"/>, each named and checked like text that takes values.</summary>
        private void CheckOutputs()
        {
            if (flow.Outputs.Count > FlowOutputs.MaxCount)
                Error(_path + ".outputs", $"A flow gives at most {FlowOutputs.MaxCount} values back.");
            foreach (var (name, text) in flow.Outputs)
            {
                foreach (var problem in FlowOutputs.Problems(flow, name, text, catalog, defaults, calls))
                    Add(problem.Severity, $"{_path}.outputs.{name}", problem.Message);
            }
        }

        /// <summary><c>ask: true</c> inputs (D-107): the wait is 5 s to 10 min, and a start with no answer stops or takes the default.</summary>
        private void CheckAsks()
        {
            foreach (var (name, input) in flow.Inputs.Where(i => i.Value.Ask && !i.Value.IsText))
            {
                var path = $"{_path}.inputs.{name}";
                if (input.Timeout is { } timeout && (timeout < Prompts.MinTimeout || timeout > Prompts.MaxTimeout))
                    Error(path + ".timeout", "Wait 5s to 10m for an answer.");
                if (input.OnTimeout.Length > 0 && input.OnTimeout is not (Prompts.Stop or Prompts.UseDefault))
                    Error(path + ".onTimeout", $"With no answer, an input can only be: {Prompts.Stop}, {Prompts.UseDefault}.");
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
            if (step.Only is { } only && only != StepNode.OnlyAlways && descriptor.Conditions.All(c => c.Id != only))
            {
                Error(path + ".only", descriptor.Conditions.Count == 0
                    ? $"{descriptor.Title} has no \"only\" condition."
                    : $"{descriptor.Title} can't be \"only: {only}\" ({string.Join(", ", descriptor.Conditions.Select(c => c.Id))}, {StepNode.OnlyAlways}).");
            }
            _step = step.Id;
            foreach (var (name, value) in step.With)
            {
                if (descriptor.FindParam(name) is { } spec)
                    CheckParam(spec, value, $"{path}.with.{name}", passThrough: descriptor.Id == ProviderCatalog.RunFlowId && name == FlowInputs.Param);
                else
                    Error($"{path}.with.{name}", $"{descriptor.Title} has no setting \"{name}\" ({Names(descriptor)}).");
            }
            foreach (var spec in descriptor.Params.Where(p => p.Required && !step.With.ContainsKey(p.Name)))
                Error(path + ".with", $"{spec.Title} is missing.");
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
            if (spec.Kind == ParamKind.LibraryRef && value is ScalarParam { Text: var result } && PickResults.StepIn(result) is not null)
            {
                if (StepValues.DeviceMissingWhy(flow, _step, result, catalog, defaults) is { } maybe)
                    Add(Severity.Warning, path, maybe);
                if (PickResults.ResultProblem(flow, _step, result, spec.Library!.Value, catalog) is { } problem)
                    Error(path, problem);
                return;
            }
            switch (spec.Kind)
            {
                case ParamKind.TextList:
                    if (value is not ListParam list)
                        Error(path, "Write a list: [a, b].");
                    else if (list.Items.Any(i => i is not ScalarParam))
                        Error(path, "List items must be plain values.");
                    else
                    {
                        CheckNoInputs(list.Items, path);
                        foreach (var item in list.Items.OfType<ScalarParam>())
                        {
                            foreach (var problem in StepValues.Problems(flow, _step, spec, item.Text, catalog, defaults, calls))
                                Add(problem.Severity, path, problem.Message);
                        }
                    }
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
            // Step values (D-129, DESIGN §4.6). Outside text settings a reference is the only error said: "isn't a number" adds nothing.
            var valueProblems = StepValues.Problems(flow, _step, spec, text, catalog, defaults, calls);
            foreach (var problem in valueProblems)
                Add(problem.Severity, path, problem.Message);
            if (valueProblems.Any(p => p.Severity == Severity.Error) && (spec.Kind == ParamKind.WholeNumber
                || !StepValues.Takes(spec) && spec.Kind is not (ParamKind.Text or ParamKind.MultilineText or ParamKind.FilePath or ParamKind.FolderPath)))
                return;
            if (spec.Required && string.IsNullOrWhiteSpace(text))
            {
                Error(path, $"{spec.Title} can't be empty.");
                return;
            }
            // One whole value in a number setting (T25.3): its range is checked when the step runs.
            if (spec.Kind == ParamKind.WholeNumber && StepValues.WholeRef(text) is not null)
                return;
            if (spec.Kind == ParamKind.LibraryRef && FlowInputs.NameIn(value) is { } name)
            {
                var kind = spec.Library!.Value;
                if (!flow.Inputs.TryGetValue(name, out var input))
                    Error(path, $"This flow has no input called \"{name}\".");
                else if (input.Kind is not { } inputKind)
                    Error(path, $"{input.Label} is text; this setting needs {WithArticle(kind)}.");
                else if (!LibraryEdits.Refers(inputKind, kind))
                    Error(path, $"{input.Label} is {WithArticle(inputKind)}; this setting needs {WithArticle(kind)}.");
                // A bound input's default must have this step's side (D-125); a missing default is the input's own error.
                else if (input.Kind == LibraryKind.Bound && kind != LibraryKind.Bound && library.Contains(LibraryKind.Bound, input.Default)
                    && !library.Contains(kind, input.Default))
                    Error(path, $"{input.Label}'s default, the bound device \"{input.Default}\", has no {LibraryName(kind)} in your Library.");
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
