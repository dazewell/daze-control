using System.Text;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Editor.ViewModels.Inspector;

/// <summary>One row of the "Insert value" list: what the person sees, and the two forms of its token.</summary>
/// <param name="Title">"Device".</param>
/// <param name="Help">"The Library name of the device it connected"; empty when there is none.</param>
/// <param name="Stored">As YAML keeps it: <c>{bt.device}</c>.</param>
/// <param name="Display">As the text box shows it: <c>{Connect Technics: Device}</c>.</param>
public sealed record ValueChoice(string Title, string Help, string Stored, string Display)
{
    /// <summary>What the value is (a number setting doesn't offer devices); null for run values, which are text.</summary>
    public ValueKind? Kind { get; init; }

    /// <summary>Narrator reads "Device, The Library name of the device it connected".</summary>
    public string AutomationName => Help.Length > 0 ? $"{Title}. {Help}" : Title;
}

/// <summary>A header and its rows in the "Insert value" list: a step's name, "This flow's inputs" or "About this run".</summary>
public sealed record ValueGroup(string Header, string Section, IReadOnlyList<ValueChoice> Rows);

/// <summary>
/// The display form of text that takes values (D-129, DESIGN §5.2). YAML stores <c>{bt.device}</c>; the box shows
/// <c>{Connect Technics: Device}</c>, the step's name and the value's title, never an id. A pure codec: both ways take the
/// flow so names and titles come from what is there now (renaming a step changes nothing stored, ids don't change).
/// Anything that names nothing, a reference to a deleted step or a hand-typed token, is kept as written. A
/// <c>{{</c> before a display token writes it literally, as in YAML.
/// </summary>
public static class ValueText
{
    public const string FromSteps = "From earlier steps";
    public const string FromInputs = "This flow's inputs";
    public const string FromRun = "About this run";

    /// <summary>"Only steps this one waits for are listed."</summary>
    public const string Footer = "Only steps this one waits for are listed.";

    /// <summary>The prefix of run values in display form: <c>{This run: Time}</c>.</summary>
    private const string RunLabel = "This run";

    /// <summary>The prefix of flow inputs in display form: <c>{Input: Output}</c>.</summary>
    private const string InputLabel = "Input";

    /// <summary>The text as the box shows it: references become <c>{Step name: Value title}</c>.</summary>
    public static string ToDisplay(Flow flow, string? stored, IReadOnlyList<ProviderDescriptor>? catalog = null, Func<string, Flow?>? flows = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (string.IsNullOrEmpty(stored) || !stored.Contains('{', StringComparison.Ordinal))
            return stored ?? "";
        var names = new Names(flow, catalog ?? ProviderCatalog.All, flows);
        var result = new StringBuilder(stored.Length + 16);
        foreach (var part in StepValues.Split(stored))
        {
            if (part.Ref is { } r)
            {
                result.Append(names.Display(r) ?? part.Text);
                continue;
            }
            // Plain text: stored-shape braces get "{{" again, and so does a brace that would read as a display token.
            var plain = StepValues.Escape(part.Text);
            for (var i = 0; i < plain.Length; i++)
            {
                if (plain[i] == '{' && names.TryDisplayToken(plain, i, out _, out _))
                    result.Append('{');
                result.Append(plain[i]);
            }
        }
        return result.ToString();
    }

    /// <summary>The stored form of what the box holds. Display tokens that name something become references.</summary>
    public static string FromDisplay(Flow flow, string? shown, IReadOnlyList<ProviderDescriptor>? catalog = null, Func<string, Flow?>? flows = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (string.IsNullOrEmpty(shown) || !shown.Contains('{', StringComparison.Ordinal))
            return shown ?? "";
        var names = new Names(flow, catalog ?? ProviderCatalog.All, flows);
        var result = new StringBuilder(shown.Length);
        var i = 0;
        while (i < shown.Length)
        {
            if (shown[i] == '{')
            {
                if (i + 1 < shown.Length && shown[i + 1] == '{' && names.TryDisplayToken(shown, i + 1, out var literal, out _))
                {
                    result.Append(shown, i + 1, literal); // "{{Connect Technics: Device}" is the text "{Connect Technics: Device}"
                    i += 1 + literal;
                    continue;
                }
                if (names.TryDisplayToken(shown, i, out var length, out var stored))
                {
                    result.Append(stored);
                    i += length;
                    continue;
                }
            }
            result.Append(shown[i++]);
        }
        return result.ToString();
    }

    /// <summary>Puts <paramref name="token"/> in <paramref name="text"/> in place of the selection; the caret goes after it.</summary>
    public static string Insert(string text, int selectionStart, int selectionLength, string token, out int caret)
    {
        text ??= "";
        var start = Math.Clamp(selectionStart, 0, text.Length);
        var length = Math.Clamp(selectionLength, 0, text.Length - start);
        caret = start + token.Length;
        return string.Concat(text.AsSpan(0, start), token, text.AsSpan(start + length));
    }

    /// <summary>
    /// The groups of the "Insert value" list for the step <paramref name="stepId"/> (DESIGN §5.1): the values of the steps it
    /// waits for, in flow order (a step's <c>error</c> only where a failure edge leads straight to this step), the flow's
    /// inputs, and the run's own values. Empty groups are left out.
    /// </summary>
    public static IReadOnlyList<ValueGroup> Choices(Flow flow, string stepId, IReadOnlyList<ProviderDescriptor>? catalog = null, Func<string, Flow?>? flows = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        catalog ??= ProviderCatalog.All;
        var self = flow.FindNode(stepId);
        var direct = self?.After.Where(e => e.When == EdgeWhen.Failure).Select(e => e.From).ToHashSet() ?? [];
        return Choices(flow, StepValues.Before(flow, stepId), direct, catalog, flows);
    }

    /// <summary>
    /// The "Insert value" list for a flow's output (T23.7): every step's values (outputs are filled when the run ends), the
    /// flow's inputs and the run's values. A step's <c>error</c> isn't offered: it has a value only when the run failed.
    /// </summary>
    public static IReadOnlyList<ValueGroup> OutputChoices(Flow flow, IReadOnlyList<ProviderDescriptor>? catalog = null, Func<string, Flow?>? flows = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return Choices(flow, [.. flow.Nodes.OfType<StepNode>()], [], catalog ?? ProviderCatalog.All, flows);
    }

    private static List<ValueGroup> Choices(Flow flow, IReadOnlyList<StepNode> steps, HashSet<string> direct, IReadOnlyList<ProviderDescriptor> catalog,
        Func<string, Flow?>? flows)
    {
        var names = new Names(flow, catalog, flows);
        var groups = new List<ValueGroup>();
        foreach (var step in steps)
        {
            if (catalog.FirstOrDefault(d => d.Id == step.Step) is not { } descriptor)
                continue;
            var label = names.LabelOf(step.Id);
            var rows = StepValues.Of(flow, step, descriptor, flows, catalog)
                .Where(v => v.Name != StepValues.ErrorName || direct.Contains(step.Id))
                .Select(v => new ValueChoice(v.Title, v.Help ?? "", $"{{{step.Id}.{v.Name}}}", $"{{{label}: {v.Title}}}") { Kind = v.Kind })
                .ToList();
            if (rows.Count > 0)
                groups.Add(new(label, FromSteps, rows));
        }
        var inputs = flow.Inputs.Select(i => new ValueChoice(names.InputLabelOf(i.Key), "", $"{{${i.Key}}}", $"{{{InputLabel}: {names.InputLabelOf(i.Key)}}}") { Kind = i.Value.Kind is null ? ValueKind.Text : ValueKind.Device }).ToList();
        if (inputs.Count > 0)
            groups.Add(new(FromInputs, FromInputs, inputs));
        groups.Add(new(FromRun, FromRun, [.. StepValues.RunNames.Select(n =>
            new ValueChoice(StepValues.RunTitle(n)!, "", $"{{@{n}}}", $"{{{RunLabel}: {StepValues.RunTitle(n)}}}"))]));
        return groups;
    }

    /// <summary>How the text boxes name a step: its name, "Connect Technics (2)" for the second of two.</summary>
    public static string LabelOf(Flow flow, string stepId, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return new Names(flow, catalog ?? ProviderCatalog.All).LabelOf(stepId);
    }

    /// <summary>What a pill shows for a reference: the value's <paramref name="Title"/> and where it comes from ("Connect Technics", "This run", "Input").</summary>
    public readonly record struct PillWords(string Title, string From);

    /// <summary>The words of a reference's pill, or null when it names nothing in this flow.</summary>
    public static PillWords? Words(Flow flow, ValueRef reference, IReadOnlyList<ProviderDescriptor>? catalog = null, Func<string, Flow?>? flows = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var names = new Names(flow, catalog ?? ProviderCatalog.All, flows);
        return reference.Source switch
        {
            ValueSource.Step => flow.FindNode(reference.Name) is StepNode s && names.Title(s, reference.Value) is { } t ? new(t, names.LabelOf(s.Id)) : null,
            ValueSource.Input => flow.Inputs.ContainsKey(reference.Name) ? new(names.InputLabelOf(reference.Name), InputLabel) : null,
            _ => StepValues.RunTitle(reference.Name) is { } title ? new(title, RunLabel) : null,
        };
    }

    /// <summary>
    /// Whether <paramref name="shown"/> holds a <c>{Step name: Value}</c> that reads like a value of this flow (a step, "This run" or
    /// "Input" before the colon) but names none: a typo or a value that is gone. It stays plain text and the box says so.
    /// </summary>
    public static bool HasUnknownValue(Flow flow, string? shown, IReadOnlyList<ProviderDescriptor>? catalog = null, Func<string, Flow?>? flows = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (string.IsNullOrEmpty(shown) || !shown.Contains('{', StringComparison.Ordinal))
            return false;
        var names = new Names(flow, catalog ?? ProviderCatalog.All, flows);
        for (var i = 0; i < shown.Length; i++)
        {
            if (shown[i] != '{')
                continue;
            // "{{Connect Technics: Nope}" is written literally (FromDisplay): not a typo.
            var escaped = i + 1 < shown.Length && shown[i + 1] == '{';
            if (names.TryDisplayToken(shown, escaped ? i + 1 : i, out var length, out _))
            {
                i += escaped ? length : length - 1;
                continue;
            }
            if (escaped)
            {
                i++;
                continue;
            }
            var close = shown.IndexOf('}', i + 1);
            var open = shown.IndexOf('{', i + 1);
            if (close < 0 || open >= 0 && open < close)
                continue;
            var body = shown[(i + 1)..close];
            var colon = body.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && names.KnownLabel(body[..colon]) && StepValues.Split(shown[i..(close + 1)]).All(p => p.Ref is null))
                return true;
        }
        return false;
    }

    /// <summary>Step labels (duplicates numbered by flow order) and the two lookups the codec needs.</summary>
    private sealed class Names
    {
        private readonly Flow _flow;
        private readonly IReadOnlyList<ProviderDescriptor> _catalog;
        private readonly Func<string, Flow?>? _flows; // a call's outputs (T23.7)
        private readonly Dictionary<string, string> _labelOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _idOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _inputLabelOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _inputKeyOf = new(StringComparer.Ordinal);

        public Names(Flow flow, IReadOnlyList<ProviderDescriptor> catalog, Func<string, Flow?>? flows = null)
        {
            _flow = flow;
            _catalog = catalog;
            _flows = flows;
            // Inputs may share a label: later ones read "Output (2)" by input order, like steps.
            foreach (var input in flow.Inputs)
            {
                var label = Safe(input.Value.Label);
                var n = 1;
                var candidate = label;
                while (_inputKeyOf.ContainsKey(candidate))
                    candidate = $"{label} ({++n})";
                _inputLabelOf[input.Key] = candidate;
                _inputKeyOf[candidate] = input.Key;
            }
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var step in flow.Nodes.OfType<StepNode>())
            {
                var name = Safe(StepValues.NameOf(step, catalog.FirstOrDefault(d => d.Id == step.Step)));
                var n = seen.GetValueOrDefault(name) + 1;
                var label = n == 1 ? name : $"{name} ({n})";
                while (_idOf.ContainsKey(label))
                    label = $"{name} ({++n})";
                seen[name] = n;
                _labelOf[step.Id] = label;
                _idOf[label] = step.Id;
            }
        }

        /// <summary>Delimiters of the display grammar in free text (step names, input labels) read as spaces, so a label always resolves back.</summary>
        private static string Safe(string text)
        {
            var clean = string.Concat(text.Select(c => c is '{' or '}' or '|' or (char)13 or (char)10 ? ' ' : c)).Trim();
            return clean.Length > 0 ? clean : "Step";
        }

        public string InputLabelOf(string key) => _inputLabelOf.GetValueOrDefault(key, key);

        public bool KnownLabel(string label) => _idOf.ContainsKey(label) || label is RunLabel or InputLabel;

        public string? Title(StepNode step, string value) => Spec(step, value)?.Title;

        public string LabelOf(string id) => _labelOf.GetValueOrDefault(id, id);

        private ValueSpec? Spec(StepNode step, string value) =>
            _catalog.FirstOrDefault(d => d.Id == step.Step) is { } d ? StepValues.Find(_flow, step, d, value, _flows, _catalog) : null;

        /// <summary>The display token of a reference, or null when it names nothing in this flow.</summary>
        public string? Display(ValueRef r)
        {
            var fallback = r.Fallback is null ? "" : "|" + r.Fallback;
            switch (r.Source)
            {
                case ValueSource.Step:
                    return _flow.FindNode(r.Name) is StepNode s && Spec(s, r.Value) is { } v
                        ? $"{{{LabelOf(s.Id)}: {v.Title}{fallback}}}" : null;
                case ValueSource.Input:
                    return _inputLabelOf.TryGetValue(r.Name, out var inputLabel)
                        ? $"{{{InputLabel}: {inputLabel}{fallback}}}" : null;
                default:
                    return StepValues.RunTitle(r.Name) is { } title ? $"{{{RunLabel}: {title}{fallback}}}" : null;
            }
        }

        /// <summary>A <c>{Label: Title|fallback}</c> at <paramref name="start"/> that names a value of this flow, as its stored reference.</summary>
        public bool TryDisplayToken(string text, int start, out int length, out string stored)
        {
            length = 0;
            stored = "";
            var end = start + 1;
            while (end < text.Length && text[end] is not ('{' or '}' or '\r' or '\n'))
                end++;
            if (end >= text.Length || text[end] != '}')
                return false;
            var body = text[(start + 1)..end];
            string? fallback = null;
            if (body.IndexOf('|', StringComparison.Ordinal) is var bar and >= 0)
            {
                fallback = body[(bar + 1)..];
                body = body[..bar];
            }
            for (var colon = body.IndexOf(": ", StringComparison.Ordinal); colon >= 0; colon = body.IndexOf(": ", colon + 1, StringComparison.Ordinal))
            {
                if (Resolve(body[..colon], body[(colon + 2)..]) is not { } reference)
                    continue;
                stored = (reference with { Fallback = fallback }).ToString();
                length = end + 1 - start;
                return true;
            }
            return false;
        }

        private ValueRef? Resolve(string label, string title)
        {
            if (_idOf.TryGetValue(label, out var id) && _flow.FindNode(id) is StepNode step)
            {
                var d = _catalog.FirstOrDefault(x => x.Id == step.Step);
                return d is null ? null
                    : StepValues.Of(_flow, step, d, _flows, _catalog).FirstOrDefault(v => v.Title == title) is { } v ? new(ValueSource.Step, id, v.Name, null, 0, 0) : null;
            }
            if (label == RunLabel)
                return StepValues.RunNames.FirstOrDefault(n => StepValues.RunTitle(n) == title) is { } run
                    ? new(ValueSource.Run, run, "", null, 0, 0) : null;
            if (label == InputLabel)
                return _inputKeyOf.TryGetValue(title, out var key) ? new(ValueSource.Input, key, "", null, 0, 0) : null;
            return null;
        }
    }
}
