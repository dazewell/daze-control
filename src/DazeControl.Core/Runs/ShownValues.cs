using DazeControl.Core.Catalog;
using DazeControl.Core.Model;

namespace DazeControl.Core.Runs;

/// <summary>
/// One line of a finished step's "Values" in Runs (T25.5, D-145): a value it gave, or a setting's text after filling.
/// A Sensitive one is <see cref="Hidden"/> and its <see cref="Text"/> is empty: the text itself is never stored (D-014).
/// </summary>
/// <param name="Title">The value's or setting's title in plain words ("Output", "Message").</param>
/// <param name="Text">What it held, cut at <see cref="ShownValues.MaxLength"/>.</param>
/// <param name="Hidden">It is Sensitive (or built from something that is): shown as hidden.</param>
public sealed record ShownValue(string Title, string Text, bool Hidden = false);

/// <summary>Builds what a finished step shows in Runs: its values and its settings' text after filling (T25.5, D-145).</summary>
public static class ShownValues
{
    /// <summary>Each text is cut here, with "…".</summary>
    public const int MaxLength = 256;

    /// <summary>A step stores at most this many lines in all (values and settings), and <see cref="MaxChars"/> characters of text,
    /// so a record's size is bounded by its step count: 100 runs of 10 steps stay near 1 MB at the most.</summary>
    public const int MaxLines = 6;

    /// <summary>The text a step may store in all (see <see cref="MaxLines"/>): four full-length values.</summary>
    public const int MaxChars = 4 * MaxLength;

    /// <summary>
    /// What a finished step shows: the values it gave (only those a later setting uses are kept by the run, D-129) and the
    /// settings whose text changed when values were filled in, as <paramref name="filled"/> holds them. Nothing derived from a
    /// Sensitive value is stored, not even masked: such a value or setting is <see cref="ShownValue.Hidden"/> with no text.
    /// </summary>
    public static (ShownValue[]? Values, ShownValue[]? Used) Of(Flow flow, StepNode written, StepNode filled, ProviderDescriptor descriptor,
        RunValues values, Library library, IReadOnlyList<(string Text, string Title)> secrets, IReadOnlyList<ProviderDescriptor> catalog,
        Func<string, Flow?>? flows)
    {
        var lines = 0;
        var chars = 0;

        ShownValue Make(string title, string text, bool hidden)
        {
            lines++;
            // A text that holds a secret is hidden whole: a mask would still keep what was built around it.
            if (hidden || StepValues.Mask(text, secrets) != text)
                return new ShownValue(title, "", Hidden: true);
            var cut = RunValues.Cut(text, Math.Min(MaxLength, MaxChars - chars));
            chars += cut.Length;
            return new ShownValue(title, cut);
        }

        bool Full() => lines >= MaxLines || chars >= MaxChars - 1;

        // A step that builds text from its settings gives values derived from whatever those settings use.
        var derived = written.Step is ProviderCatalog.MakeTextId or ProviderCatalog.ValueCheckId or ProviderCatalog.ExtractId
            && written.With.Values.Any(v => TextsOf(v).Any(t => MentionsSecret(flow, t, values, secrets, catalog, flows)));
        var gave = new List<ShownValue>();
        foreach (var spec in StepValues.Of(flow, written, descriptor, flows, catalog))
        {
            if (Full() || !values.TryGet(written.Id, spec.Name, out var given))
                continue;
            var text = StepValues.Show(given, library);
            if (text.Length == 0)
                continue;
            gave.Add(Make(spec.Title, text,
                spec.Sensitive || values.IsMarkedSensitive(written.Id, spec.Name) || (derived && spec.Name != StepValues.ErrorName)));
        }

        var used = new List<ShownValue>();
        foreach (var (key, was) in written.With)
        {
            if (Full() || descriptor.FindParam(key) is not { } spec || !filled.With.TryGetValue(key, out var now) || Equals(was, now))
                continue;
            var text = string.Join(", ", TextsOf(now));
            if (text.Length == 0)
                continue;
            // Hidden whole when the setting mentions a Sensitive value: a fill rule changes the text (percent-encoding in an
            // address), which masking would not find.
            used.Add(Make(spec.Title, text, TextsOf(was).Any(t => MentionsSecret(flow, t, values, secrets, catalog, flows))));
        }
        return (gave.Count == 0 ? null : [.. gave], used.Count == 0 ? null : [.. used]);
    }

    private static IEnumerable<string> TextsOf(ParamValue value) => value switch
    {
        ScalarParam { Text: var t } => [t],
        ListParam list => list.Items.OfType<ScalarParam>().Select(i => i.Text),
        _ => [],
    };

    private static bool MentionsSecret(Flow flow, string text, RunValues values, IReadOnlyList<(string Text, string Title)> secrets,
        IReadOnlyList<ProviderDescriptor> catalog, Func<string, Flow?>? flows)
    {
        if (StepValues.Mask(text, secrets) != text)
            return true;
        foreach (var r in StepValues.Parse(text))
        {
            switch (r.Source)
            {
                case ValueSource.Input:
                    if (values.SensitiveInputs().Any(s => s.Name == r.Name))
                        return true;
                    break;
                case ValueSource.Step:
                    if (flow.FindNode(r.Name) is StepNode producer
                        && (values.IsMarkedSensitive(r.Name, r.Value)
                            || (catalog.FirstOrDefault(d => d.Id == producer.Step) is { } d
                                && StepValues.Find(flow, producer, d, r.Value, flows, catalog)?.Sensitive == true)))
                        return true;
                    break;
            }
        }
        return false;
    }
}
