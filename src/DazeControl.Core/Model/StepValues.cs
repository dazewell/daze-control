using System.Text;
using DazeControl.Core.Catalog;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;

namespace DazeControl.Core.Model;

/// <summary>Where a <see cref="ValueRef"/> takes its value from.</summary>
public enum ValueSource
{
    /// <summary><c>{bt.device}</c>: a value an earlier step gave.</summary>
    Step,

    /// <summary><c>{$output}</c>: the flow input's device name.</summary>
    Input,

    /// <summary><c>{@time}</c>: a value of the run itself.</summary>
    Run,
}

/// <summary>
/// One reference in text (D-129). <see cref="Name"/> is the step id, input name or run value name; <see cref="Value"/>
/// the step's value name (empty for inputs and run values). <see cref="Start"/>/<see cref="Length"/> locate it in the text it
/// was parsed from. <see cref="Fallback"/> is the <c>|text</c> shown when the value is missing or empty (null = none written).
/// </summary>
public readonly record struct ValueRef(ValueSource Source, string Name, string Value, string? Fallback, int Start, int Length)
{
    /// <summary>As YAML writes it: <c>{bt.device|unknown}</c>.</summary>
    public override string ToString()
    {
        var head = Source switch
        {
            ValueSource.Input => "$" + Name,
            ValueSource.Run => "@" + Name,
            _ => Name + "." + Value,
        };
        return "{" + head + (Fallback is null ? "" : "|" + Fallback) + "}";
    }
}

/// <summary>A piece of text: plain <see cref="Text"/> (escapes already undone) when <see cref="Ref"/> is null, else a reference.</summary>
public readonly record struct TextPart(string Text, ValueRef? Ref);

/// <summary>Something wrong with one reference in a setting (DESIGN §4.6); the validator adds the YAML path.</summary>
public readonly record struct ValueProblem(Severity Severity, string Message, ValueRef Ref);

/// <summary>
/// Step values (D-129, docs/step-values/DESIGN.md): the one resolver the validator, runner, dry run and editor share.
/// Text settings marked <see cref="ParamSpec.TakesValues"/> may hold <c>{step.value}</c>, <c>{$input}</c> and <c>{@run}</c>,
/// each with an optional <c>|fallback</c>; <c>{{</c> before a reference shape writes it literally. Anything that isn't a
/// reference shape is plain text, so scripts, JSON and <c>%VAR%</c> keep their meaning. <c>$name</c> (D-064) and
/// <c>$result:&lt;id&gt;</c> (D-117) stay whole-value forms for device settings.
/// </summary>
public static class StepValues
{
    /// <summary>The value every step gives when it fails.</summary>
    public const string ErrorName = "error";

    /// <summary>A Pick's value, and what <c>$result:&lt;id&gt;</c> means.</summary>
    public const string DeviceName = "device";

    public const string RunFlow = "flow", RunTrigger = "trigger", RunTime = "time", RunDate = "date";

    /// <summary>A filled setting is cut here, with "…" (DESIGN §4.4).</summary>
    public const int MaxFilledLength = 4000;

    /// <summary>"Why it failed": every step's <c>error</c>, given only when it fails.</summary>
    public static readonly ValueSpec Error = new(ErrorName, ValueKind.Text, "Why it failed")
    {
        OnFailure = true,
        Help = "What went wrong, when the step fails",
    };

    /// <summary>The run values in the order the editor lists them.</summary>
    public static readonly IReadOnlyList<string> RunNames = [RunFlow, RunTrigger, RunTime, RunDate];

    /// <summary>"Flow name" for <c>@flow</c>; null for an unknown name.</summary>
    public static string? RunTitle(string name) => name switch
    {
        RunFlow => "Flow name",
        RunTrigger => "What started it",
        RunTime => "Time",
        RunDate => "Date",
        _ => null,
    };

    /// <summary>The values a step type gives: its own, then <c>error</c> (unless it declares one itself).</summary>
    public static IReadOnlyList<ValueSpec> Of(ProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Values.Any(v => v.Name == ErrorName) ? [.. descriptor.Values] : [.. descriptor.Values, Error];
    }

    public static ValueSpec? Find(ProviderDescriptor descriptor, string name) => Of(descriptor).FirstOrDefault(v => v.Name == name);

    /// <summary>Whether a setting fills references: marked <see cref="ParamSpec.TakesValues"/> and a text kind.</summary>
    public static bool Takes(ParamSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.TakesValues && spec.Kind is ParamKind.Text or ParamKind.MultilineText;
    }

    /// <summary>Whether each item of a list setting fills references (<c>process.run</c>'s arguments): one argument per item.</summary>
    public static bool TakesItems(ParamSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.TakesValues && spec.Kind == ParamKind.TextList;
    }

    // ---- Grammar (DESIGN §4.3) ----

    /// <summary>The references in <paramref name="text"/>, in order; escaped ones (<c>{{bt.device}</c>) aren't.</summary>
    public static IReadOnlyList<ValueRef> Parse(string? text)
    {
        var found = new List<ValueRef>();
        if (string.IsNullOrEmpty(text) || !text.Contains('{', StringComparison.Ordinal))
            return found;
        foreach (var part in Split(text))
        {
            if (part.Ref is { } r)
                found.Add(r);
        }
        return found;
    }

    /// <summary>The text as plain pieces and references; plain pieces have <c>{{</c> escapes undone.</summary>
    public static IReadOnlyList<TextPart> Split(string? text)
    {
        var parts = new List<TextPart>();
        if (string.IsNullOrEmpty(text))
            return parts;
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                plain.Append(text[i++]);
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == '{' && TryRef(text, i + 1, out var escaped))
            {
                plain.Append(text, i + 1, escaped.Length); // "{{bt.device}" → "{bt.device}"
                i += 1 + escaped.Length;
                continue;
            }
            if (TryRef(text, i, out var found))
            {
                if (plain.Length > 0)
                    parts.Add(new(plain.ToString(), null));
                plain.Clear();
                parts.Add(new(text.Substring(i, found.Length), found));
                i += found.Length;
                continue;
            }
            plain.Append(text[i++]);
        }
        if (plain.Length > 0)
            parts.Add(new(plain.ToString(), null));
        return parts;
    }

    /// <summary>Plain text made safe to store in a setting that takes values: every reference shape gets a <c>{{</c>.</summary>
    public static string Escape(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        if (!literal.Contains('{', StringComparison.Ordinal))
            return literal;
        var result = new StringBuilder(literal.Length + 4);
        for (var i = 0; i < literal.Length; i++)
        {
            // Only the "{" that starts a shape is doubled: "{{bt.device}" as plain text is stored "{{{bt.device}".
            if (literal[i] == '{' && TryRef(literal, i, out _))
                result.Append('{');
            result.Append(literal[i]);
        }
        return result.ToString();
    }

    private static bool TryRef(string text, int start, out ValueRef found)
    {
        found = default;
        var i = start + 1;
        if (i >= text.Length)
            return false;
        ValueSource source;
        string name, value = "";
        if (text[i] is '$' or '@')
        {
            source = text[i] == '$' ? ValueSource.Input : ValueSource.Run;
            i++;
            var begin = i;
            if (i >= text.Length || !char.IsAsciiLetterLower(text[i]))
                return false;
            i++;
            while (i < text.Length && (source == ValueSource.Input
                       ? char.IsAsciiLetterLower(text[i]) || char.IsAsciiDigit(text[i]) || text[i] == '-'
                       : char.IsAsciiLetterOrDigit(text[i])))
                i++;
            name = text[begin..i];
        }
        else
        {
            source = ValueSource.Step;
            var begin = i;
            if (!char.IsAsciiLetterOrDigit(text[i]))
                return false;
            i++;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '-'))
                i++;
            name = text[begin..i];
            if (i >= text.Length || text[i] != '.')
                return false;
            i++;
            var valueStart = i;
            if (i >= text.Length || !char.IsAsciiLetterLower(text[i]))
                return false;
            i++;
            while (i < text.Length && char.IsAsciiLetterOrDigit(text[i]))
                i++;
            value = text[valueStart..i];
        }
        string? fallback = null;
        if (i < text.Length && text[i] == '|')
        {
            var begin = ++i;
            while (i < text.Length && text[i] is not ('{' or '}' or '\r' or '\n'))
                i++;
            fallback = text[begin..i];
        }
        if (i >= text.Length || text[i] != '}')
            return false;
        found = new(source, name, value, fallback, start, i + 1 - start);
        return true;
    }

    // ---- Order (one reachability rule, shared with PickResults) ----

    /// <summary>
    /// The steps that always finish before <paramref name="nodeId"/> starts: ones it waits for, directly or through other
    /// steps and cards (any edge), and the members of cards it waits for. Its own card's members start with it, so they
    /// don't count. In flow order.
    /// </summary>
    public static IReadOnlyList<StepNode> Before(Flow flow, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        void Enqueue(Node? node)
        {
            if (node is null)
                return;
            foreach (var edge in node.After)
            {
                if (seen.Add(edge.From))
                    queue.Enqueue(edge.From);
            }
        }
        Enqueue(flow.FindNode(FlowShape.UnitOf(flow, nodeId)));
        while (queue.Count > 0)
        {
            var node = flow.FindNode(queue.Dequeue());
            if (node is GroupNode card)
            {
                foreach (var member in card.Members)
                    seen.Add(member);
            }
            Enqueue(node);
        }
        return [.. flow.Nodes.OfType<StepNode>().Where(s => seen.Contains(s.Id))];
    }

    /// <summary>What is sure about the producer whenever the consumer starts.</summary>
    private enum Known { Nothing, Ran, Succeeded, Failed }

    /// <summary>
    /// Whether <paramref name="value"/> of <paramref name="producerId"/> can be missing when <paramref name="consumerId"/>
    /// runs, although the producer always finishes first (DESIGN §4.5): the producer has an "Only if", runs the next steps
    /// anyway after failing (or its card does), or every way to the consumer passes an <c>always</c> edge or starts with a
    /// <c>failure</c> edge. <c>error</c> is there only when the producer surely failed; <see cref="ValueSpec.OnFailure"/>
    /// values whenever it surely ran. A fallback producer reached through success edges is fine: the consumer ran, so it did.
    /// </summary>
    /// <param name="defaults"><c>settings.stepDefaults</c>, for steps that inherit their policy or "Only if" (D-080).</param>
    public static bool MayBeMissing(Flow flow, string consumerId, string producerId, ValueSpec value,
        ValueDictionary<StepDefault>? defaults = null) => MissingWhy(flow, consumerId, producerId, value, defaults) is not null;

    private static string? MissingWhy(Flow flow, string consumerId, string producerId, ValueSpec value, ValueDictionary<StepDefault>? defaults)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(value);
        if (flow.FindNode(producerId) is not StepNode producer)
            return "missing";
        StepDefault? typeDefault = defaults is { } d && d.TryGetValue(producer.Step, out var found) ? found : null;
        var policy = producer.OnFailure ?? typeDefault?.OnFailure ?? FailurePolicy.Stop;
        var only = (producer.Only ?? typeDefault?.Only) is { } o && o != StepNode.OnlyAlways;
        var card = flow.Nodes.OfType<GroupNode>().FirstOrDefault(c => c.Members.Contains(producerId));
        var cardIgnores = card?.OnFailure == FailurePolicy.Ignore;

        // The scheduler's joins (D-112): an edge from a branch that may simply not be taken (a fallback that ends NotNeeded,
        // a unit whose failure a failure edge handles, or one skipped only because of such a branch) is an alternative,
        // so a unit with several edges can start without it firing. Only the other edges, or a unit's only edge, are sure.
        var fallbacks = FlowShape.Fallbacks(flow);
        var withFallback = FlowShape.WithFallback(flow);
        var excusable = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Untakeable(string unit)
        {
            if (fallbacks.Contains(unit) || withFallback.Contains(unit))
                return true;
            if (excusable.TryGetValue(unit, out var found))
                return found;
            excusable[unit] = false;
            return excusable[unit] = (flow.FindNode(unit)?.After ?? []).Any(e => Untakeable(e.From));
        }
        var memo = new Dictionary<string, Known>(StringComparer.Ordinal);
        Known At(string unit)
        {
            if (memo.TryGetValue(unit, out var known))
                return known;
            memo[unit] = Known.Nothing; // a loop (refused elsewhere) proves nothing
            known = Known.Nothing;
            var edges = flow.FindNode(unit)?.After ?? [];
            foreach (var edge in edges)
            {
                if (edges.Count == 1 || !Untakeable(edge.From))
                    known = Stronger(known, Via(edge));
            }
            return memo[unit] = known;
        }
        Known Via(Edge edge)
        {
            if (edge.When == EdgeWhen.Always)
                return Known.Nothing; // fires from skipped and failed steps too (FEATURES F1)
            if (edge.From == producerId)
                return edge.When == EdgeWhen.Failure ? Known.Failed : only ? Known.Nothing : policy == FailurePolicy.Ignore ? Known.Ran : Known.Succeeded;
            if (card is not null && edge.From == card.Id)
            {
                if (only)
                    return Known.Nothing;
                return edge.When == EdgeWhen.Failure || policy == FailurePolicy.Ignore || cardIgnores ? Known.Ran : Known.Succeeded;
            }
            return At(edge.From); // that unit ran (it succeeded, failed, or ignored a failure), so what held at its start holds
        }
        var sure = At(FlowShape.UnitOf(flow, consumerId));
        var name = $"\"{NameOf(producer)}\"";
        if (value.Name == ErrorName)
            return sure == Known.Failed ? null : $"{value.Title} has a value only when {name} fails";
        if (sure == Known.Succeeded || value.OnFailure && sure != Known.Nothing)
            return null;
        if (only)
            return $"{name} may be skipped (Only if), so {value.Title} can be empty";
        if (policy == FailurePolicy.Ignore || cardIgnores)
            return $"{name} may not give it (Run the next steps anyway), so {value.Title} can be empty";
        return $"{name} may fail or be skipped on the way to this step, so {value.Title} can be empty";
    }

    /// <summary>Both hold (every sure edge fired); Succeeded and Failed of one step can't meet, so either wins.</summary>
    private static Known Stronger(Known a, Known b) => a == Known.Nothing || a == Known.Ran && b != Known.Nothing ? b : a;

    // ---- Validation (DESIGN §4.6) ----

    /// <summary>
    /// What is wrong with the references in <paramref name="text"/>, a setting of step <paramref name="stepId"/> (DESIGN §4.6).
    /// A setting that takes values gets every check; another text setting warns only about references that would work;
    /// a device, number, choice or duration setting gets an error for them. Unknown step ids only warn (old text that
    /// looked like a reference stays as written).
    /// </summary>
    public static IReadOnlyList<ValueProblem> Problems(Flow flow, string stepId, ParamSpec spec, string text,
        IReadOnlyList<ProviderDescriptor>? catalog = null, ValueDictionary<StepDefault>? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(spec);
        var problems = new List<ValueProblem>();
        var refs = Parse(text);
        if (refs.Count == 0 || spec.Kind == ParamKind.TextMap || spec.Kind == ParamKind.TextList && !TakesItems(spec))
            return problems;
        catalog ??= ProviderCatalog.All;
        var takes = Takes(spec) || TakesItems(spec);
        var textKind = spec.Kind is ParamKind.Text or ParamKind.MultilineText or ParamKind.FilePath or ParamKind.FolderPath;
        foreach (var r in refs)
        {
            var why = Check(flow, stepId, r, catalog, defaults, out var warning);
            if (takes)
            {
                if (why is not null)
                    problems.Add(new(warning ? Severity.Warning : Severity.Error, why, r));
            }
            else if (Exists(flow, r, catalog))
            {
                problems.Add(textKind
                    ? new(Severity.Warning, $"This setting doesn't take values; {r} is used as written.", r)
                    : new(Severity.Error, $"Values from earlier steps only go in text settings; {spec.Title} can't use {r}.", r));
            }
        }
        return problems;
    }

    /// <summary>Whether the reference names something real (so outside a setting that takes values it is worth a word).</summary>
    private static bool Exists(Flow flow, ValueRef r, IReadOnlyList<ProviderDescriptor> catalog) => r.Source switch
    {
        ValueSource.Input => flow.Inputs.ContainsKey(r.Name),
        ValueSource.Run => RunTitle(r.Name) is not null,
        _ => flow.FindNode(r.Name) is StepNode s && Descriptor(catalog, s) is { } d && Find(d, r.Value) is not null,
    };

    private static string? Check(Flow flow, string stepId, ValueRef r, IReadOnlyList<ProviderDescriptor> catalog,
        ValueDictionary<StepDefault>? defaults, out bool warning)
    {
        warning = false;
        switch (r.Source)
        {
            case ValueSource.Input:
                return flow.Inputs.ContainsKey(r.Name) ? null : $"This flow has no input called \"{r.Name}\".";
            case ValueSource.Run:
                return RunTitle(r.Name) is not null ? null
                    : $"There's no run value called \"{r.Name}\" ({string.Join(", ", RunNames)}).";
        }
        if (flow.FindNode(r.Name) is not StepNode producer)
        {
            warning = true;
            return $"There's no step called \"{r.Name}\" in this flow, so {r} shows as written.";
        }
        if (Descriptor(catalog, producer) is not { } descriptor)
            return null; // the unknown step type is its own error
        var name = NameOf(producer, descriptor);
        if (Find(descriptor, r.Value) is not { } value)
            return $"{name} gives no value called \"{r.Value}\" (it gives {string.Join(", ", Of(descriptor).Select(v => v.Title))}).";
        if (producer.Id == stepId)
            return $"A step can't use its own values: {r}.";
        if (FlowShape.UnitOf(flow, producer.Id) is var card && card != producer.Id && card == FlowShape.UnitOf(flow, stepId))
            return $"\"{name}\" starts together with this step (same card), so its values aren't ready: move one of them out of the card.";
        if (Before(flow, stepId).All(s => s.Id != producer.Id))
            return $"\"{name}\" must run before this step: connect it so this step comes after.";
        if (r.Fallback is null && MissingWhy(flow, stepId, producer.Id, value, defaults) is { } missing)
        {
            warning = true;
            return missing + ". Add what to show when it has no value.";
        }
        return null;
    }

    /// <summary>
    /// Why value <paramref name="valueName"/> of <paramref name="producerId"/> can't fill <paramref name="stepId"/>'s device setting
    /// needing <paramref name="setting"/>, or null: the value must be a Device that <see cref="PickResults.Fits"/> (D-116), and the
    /// producer must always finish first. For <c>$result:&lt;id&gt;.&lt;value&gt;</c> (T23.6); <c>$result:&lt;id&gt;</c> keeps
    /// <see cref="PickResults.Problem"/>.
    /// </summary>
    public static string? DeviceProblem(Flow flow, string stepId, string producerId, string valueName, LibraryKind setting,
        IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        catalog ??= ProviderCatalog.All;
        if (flow.FindNode(producerId) is not StepNode producer || Descriptor(catalog, producer) is not { } descriptor)
            return $"There's no step called \"{producerId}\" in this flow.";
        var name = NameOf(producer, descriptor);
        if (Find(descriptor, valueName) is not { } value)
            return $"{name} gives no value called \"{valueName}\" (it gives {string.Join(", ", Of(descriptor).Select(v => v.Title))}).";
        if (value.Kind != ValueKind.Device)
            return $"{value.Title} of \"{name}\" is text; this setting needs {Article(setting)}.";
        var kind = value.Library ?? (PickResults.IsPick(producer) ? PickResults.KindOf(producer) : null);
        if (kind is { } given && !PickResults.Fits(given, setting))
            return $"{value.Title} of \"{name}\" is {Article(given)}; this setting needs {Article(setting)}.";
        if (Before(flow, stepId).All(s => s.Id != producerId))
            return $"\"{name}\" must run before this step: connect it so this step comes after.";
        return null;
    }

    // ---- Editing a flow (DESIGN §5.6) ----

    /// <summary>One reference in a step's setting: who reads it, in which setting.</summary>
    public readonly record struct Use(StepNode Consumer, ParamSpec Setting, ValueRef Ref);

    /// <summary>Every <c>{step.value}</c>, <c>{$input}</c> and <c>{@run}</c> in the settings of the flow that take values, in flow order.</summary>
    public static IReadOnlyList<Use> Uses(Flow flow, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        catalog ??= ProviderCatalog.All;
        var uses = new List<Use>();
        foreach (var step in flow.Nodes.OfType<StepNode>())
        {
            if (Descriptor(catalog, step) is not { } descriptor)
                continue;
            foreach (var (name, setting) in step.With)
            {
                if (setting is ScalarParam { Text: var text } && descriptor.FindParam(name) is { } spec && Takes(spec))
                    uses.AddRange(Parse(text).Select(r => new Use(step, spec, r)));
            }
        }
        return uses;
    }

    /// <summary>
    /// <paramref name="text"/> with each reference <paramref name="replace"/> answers for swapped for what it returns, written as
    /// stored (null keeps the reference). Plain text is kept as it was; text with nothing replaced comes back unchanged.
    /// </summary>
    public static string Rewrite(string text, Func<ValueRef, string?> replace)
    {
        ArgumentNullException.ThrowIfNull(replace);
        var changed = false;
        var result = new StringBuilder(text.Length);
        foreach (var part in Split(text))
        {
            if (part.Ref is { } r && replace(r) is { } stored)
            {
                result.Append(stored);
                changed = true;
            }
            else
            {
                result.Append(part.Ref is null ? Escape(part.Text) : part.Text);
            }
        }
        return changed ? result.ToString() : text;
    }

    /// <summary>
    /// The flow with <paramref name="replace"/> applied to every reference in its settings that take values (see
    /// <see cref="Rewrite"/>); the same instance when nothing changed.
    /// </summary>
    public static Flow RewriteRefs(Flow flow, Func<StepNode, ValueRef, string?> replace, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        catalog ??= ProviderCatalog.All;
        var nodes = new List<Node>(flow.Nodes.Count);
        var any = false;
        foreach (var node in flow.Nodes)
        {
            if (node is not StepNode step || Descriptor(catalog, step) is not { } descriptor)
            {
                nodes.Add(node);
                continue;
            }
            Dictionary<string, ParamValue>? changed = null;
            foreach (var (name, setting) in step.With)
            {
                if (setting is not ScalarParam { Text: var text } || descriptor.FindParam(name) is not { } spec || !Takes(spec))
                    continue;
                var next = Rewrite(text, r => replace(step, r));
                if (!ReferenceEquals(next, text))
                    (changed ??= new(step.With, StringComparer.Ordinal))[name] = new ScalarParam(next);
            }
            if (changed is null)
            {
                nodes.Add(node);
                continue;
            }
            any = true;
            nodes.Add(step with { With = new(changed) });
        }
        return any ? flow with { Nodes = [.. nodes] } : flow;
    }

    /// <summary>
    /// Deleting steps: their references become the fallback text (nothing when none was written), so no setting keeps
    /// pointing at a step that is gone.
    /// </summary>
    public static Flow DropRefsTo(Flow flow, IReadOnlySet<string> goneIds, IReadOnlyList<ProviderDescriptor>? catalog = null) =>
        goneIds.Count == 0 ? flow : RewriteRefs(flow, (_, r) => r.Source == ValueSource.Step && goneIds.Contains(r.Name) ? Escape(r.Fallback ?? "") : null, catalog);

    // ---- Run time (DESIGN §4.7) ----

    /// <summary>
    /// The values some setting of the flow references: <c>{step.value}</c> in settings that take values and
    /// <c>$result:&lt;id&gt;</c> (= the step's <c>device</c>) anywhere. The runner keeps only these.
    /// </summary>
    public static IReadOnlySet<(string Step, string Value)> Wanted(Flow flow, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        catalog ??= ProviderCatalog.All;
        var wanted = new HashSet<(string Step, string Value)>();
        foreach (var step in flow.Nodes.OfType<StepNode>())
        {
            var descriptor = Descriptor(catalog, step);
            foreach (var (name, setting) in step.With)
            {
                var values = setting is MapParam map ? map.Entries.Values : [setting];
                foreach (var v in values)
                {
                    if (PickResults.StepIn(v) is { } pick)
                        wanted.Add((pick, DeviceName));
                }
                if (descriptor?.FindParam(name) is not { } spec)
                    continue;
                foreach (var text in TextsOf(setting, spec))
                {
                    foreach (var r in Parse(text).Where(r => r.Source == ValueSource.Step))
                        wanted.Add((r.Name, r.Value));
                }
            }
            if (step.Step == ProviderCatalog.PowerShellId && step.With.TryGetValue("script", out var script) && script is ScalarParam { Text: var code })
            {
                // A script reads values from its environment (DESIGN §4.8): keep the ones its text names.
                foreach (var producer in Before(flow, step.Id))
                {
                    if (Descriptor(catalog, producer) is not { } d)
                        continue;
                    foreach (var v in Of(d).Where(v => code.Contains(Variable(producer.Id, v.Name), StringComparison.OrdinalIgnoreCase)))
                        wanted.Add((producer.Id, v.Name));
                }
            }
        }
        return wanted;
    }

    /// <summary>The texts of a setting that fill references: one for a text setting, each item of a list whose items fill.</summary>
    private static IEnumerable<string> TextsOf(ParamValue setting, ParamSpec spec) => setting switch
    {
        ScalarParam { Text: var text } when Takes(spec) => [text],
        ListParam list when TakesItems(spec) => list.Items.OfType<ScalarParam>().Select(i => i.Text),
        _ => [],
    };

    /// <summary>The environment variable a script reads a value from: <c>DAZE_BT_DEVICE</c> for <c>bt</c>'s <c>device</c> (id <c>-</c> → <c>_</c>).</summary>
    public static string Variable(string step, string value) =>
        "DAZE_" + step.Replace('-', '_').ToUpperInvariant() + "_" + value.ToUpperInvariant();

    /// <summary>
    /// What a script step gets as environment variables (DESIGN §4.8): every value its earlier steps gave, as text (a Device shows
    /// its Library name). The script's text is never changed, so a device named <c>'; Remove-Item …</c> stays data.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Variables(Flow flow, string stepId, RunValues values, Library library,
        IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(library);
        catalog ??= ProviderCatalog.All;
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Only what this script's text names (as Wanted does): a value another step uses, a Sensitive one included, isn't handed to every script.
        if (flow.FindNode(stepId) is not StepNode { With: var with } || !with.TryGetValue("script", out var script) || script is not ScalarParam { Text: var code })
            return variables;
        foreach (var producer in Before(flow, stepId))
        {
            if (Descriptor(catalog, producer) is not { } d)
                continue;
            foreach (var v in Of(d))
            {
                if (code.Contains(Variable(producer.Id, v.Name), StringComparison.OrdinalIgnoreCase) && values.TryGet(producer.Id, v.Name, out var given))
                    variables[Variable(producer.Id, v.Name)] = Show(given, library).Replace("\0", "", StringComparison.Ordinal);
            }
        }
        return variables;
    }

    /// <summary>
    /// The Sensitive values (<see cref="ValueSpec.Sensitive"/>) that earlier steps gave, with their titles, so the runner can keep
    /// them out of errors and summaries (D-014): see <see cref="Mask"/>.
    /// </summary>
    public static IReadOnlyList<(string Text, string Title)> Secrets(Flow flow, string stepId, RunValues values, Library library,
        IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(library);
        catalog ??= ProviderCatalog.All;
        var secrets = new List<(string Text, string Title)>();
        foreach (var producer in Before(flow, stepId))
        {
            if (Descriptor(catalog, producer) is not { } d)
                continue;
            foreach (var v in Of(d).Where(v => v.Sensitive))
            {
                if (values.TryGet(producer.Id, v.Name, out var given) && Show(given, library) is { Length: > 0 } text)
                    secrets.Add((text, v.Title));
            }
        }
        // Longest first, so a secret that contains another is hidden whole.
        return [.. secrets.OrderByDescending(s => s.Text.Length)];
    }

    /// <summary>The text with each secret shown as its title ("‹Output›"); null stays null.</summary>
    public static string? Mask(string? text, IReadOnlyList<(string Text, string Title)> secrets)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var (secret, title) in secrets)
            text = text.Replace(secret, "‹" + title + "›", StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// <c>%</c> in a value filled into an <see cref="FillRule.Argument"/> becomes this private character, so the provider's
    /// <c>%VAR%</c> expansion of the written text never reads a value; <see cref="Unguard"/> puts the <c>%</c> back.
    /// </summary>
    public const char PercentGuard = '\uE000';

    public static string Unguard(string text) => text.Replace(PercentGuard, '%');
    /// <summary>
    /// The step as its provider should see it: settings that take values filled from <paramref name="values"/> (Device values
    /// show the Library item's name), and <c>$result:&lt;id&gt;</c> device settings swapped for the key (as
    /// <see cref="PickResults.Resolve"/>). A missing value shows its fallback, else nothing; a reference to a step that isn't
    /// in the flow stays as written. <paramref name="missing"/> names a step whose device a device setting needed but didn't get.
    /// Nothing to fill: the same instance.
    /// </summary>
    public static StepNode Fill(Flow flow, StepNode step, RunValues values, Library library, out string? missing,
        ProviderDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(library);
        descriptor ??= ProviderCatalog.Find(step.Step);
        var filled = step;
        if (descriptor is not null && step.With.Any(e => descriptor.FindParam(e.Key) is { } spec && TextsOf(e.Value, spec).Any(t => t.Contains('{', StringComparison.Ordinal))))
        {
            filled = step with
            {
                With = new(step.With.Select(e => KeyValuePair.Create(e.Key, descriptor.FindParam(e.Key) is { } spec ? FillParam(flow, e.Value, spec, values, library) : e.Value))),
            };
        }
        return PickResults.Resolve(filled, values.Picked(), out missing, descriptor);
    }

    private static ParamValue FillParam(Flow flow, ParamValue setting, ParamSpec spec, RunValues values, Library library) => setting switch
    {
        ScalarParam { Text: var text } when Takes(spec) => new ScalarParam(FillText(flow, text, values, library, spec.Kind == ParamKind.Text, spec.Fill)),
        ListParam list when TakesItems(spec) => new ListParam(new ValueList<ParamValue>(list.Items.Select(i =>
            i is ScalarParam { Text: var item } ? new ScalarParam(FillText(flow, item, values, library, singleLine: false, spec.Fill)) : i))),
        _ => setting,
    };

    /// <summary>One text with its references filled (see <see cref="Fill"/>); cut at <see cref="MaxFilledLength"/>.</summary>
    /// <param name="rule">How a value is written: percent-encoded in an address, <c>%</c> guarded in a program argument.</param>
    public static string FillText(Flow flow, string text, RunValues values, Library library, bool singleLine = true, FillRule rule = FillRule.None)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(library);
        var result = new StringBuilder();
        foreach (var part in Split(text))
        {
            if (part.Ref is not { } r)
            {
                result.Append(part.Text);
                continue;
            }
            if (r.Source == ValueSource.Step && flow.FindNode(r.Name) is not StepNode)
            {
                result.Append(part.Text);
                continue;
            }
            var value = r.Source switch
            {
                ValueSource.Input => values.TryGetInput(r.Name, out var input) ? Show(input, library) : null,
                ValueSource.Run => values.Run(r.Name),
                _ => values.TryGet(r.Name, r.Value, out var found) ? Show(found, library) : null,
            };
            var given = !string.IsNullOrEmpty(value);
            value = given ? value! : r.Fallback ?? "";
            if (singleLine)
                value = value.ReplaceLineEndings(" ");
            if (rule != FillRule.None && given)
                value = rule == FillRule.Url ? Uri.EscapeDataString(value) : value.Replace('%', PercentGuard);
            result.Append(value);
        }
        return RunValues.Cut(result.ToString(), MaxFilledLength);
    }

    /// <summary>A value as text: a Device shows its Library name (the key when it has none).</summary>
    public static string Show(StepValue value, Library library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return value.Kind == ValueKind.Device ? DisplayName(library, value.Library, value.Text) : value.Text;
    }

    private static string DisplayName(Library library, LibraryKind? kind, string key)
    {
        var bound = library.Bound.FirstOrDefault(b => b.Key == key)?.Name;
        var bluetooth = library.Bluetooth.FirstOrDefault(b => b.Key == key)?.Name;
        return kind switch
        {
            LibraryKind.Bluetooth => bluetooth ?? bound,
            LibraryKind.Bound or LibraryKind.Audio => bound,
            LibraryKind.Display or LibraryKind.Android => null,
            _ => bound ?? bluetooth,
        } ?? key;
    }

    /// <summary>
    /// The text with each reference shown as its title, for dry runs and summaries of Sensitive values:
    /// "Connected to ‹Device› at ‹Time›" (without <paramref name="marks"/>: "Connected to Device at Time", for the canvas). References that name nothing stay as written.
    /// </summary>
    public static string Describe(Flow flow, string text, IReadOnlyList<ProviderDescriptor>? catalog = null, bool marks = true)
    {
        ArgumentNullException.ThrowIfNull(flow);
        catalog ??= ProviderCatalog.All;
        var result = new StringBuilder();
        foreach (var part in Split(text))
        {
            var title = part.Ref is not { } r ? null : r.Source switch
            {
                ValueSource.Input => flow.Inputs.TryGetValue(r.Name, out var input) ? input.Label : null,
                ValueSource.Run => RunTitle(r.Name),
                _ => flow.FindNode(r.Name) is StepNode s && Descriptor(catalog, s) is { } d ? Find(d, r.Value)?.Title : null,
            };
            result.Append(title is null ? part.Text : marks ? "‹" + title + "›" : title);
        }
        return result.ToString();
    }

    /// <summary>The step's name on the canvas, else its type's title, else its id.</summary>
    public static string NameOf(StepNode step, ProviderDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Name is { Length: > 0 } name ? name : (descriptor ?? ProviderCatalog.Find(step.Step))?.Title ?? step.Id;
    }

    private static ProviderDescriptor? Descriptor(IReadOnlyList<ProviderDescriptor> catalog, StepNode step) =>
        catalog.FirstOrDefault(d => d.Id == step.Step);

    private static string Article(LibraryKind kind) =>
        (kind is LibraryKind.Audio or LibraryKind.Android ? "an " : "a ") + Library.KindName(kind);
}
