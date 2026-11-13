using DazeControl.Core.Catalog;
using DazeControl.Core.Model;

namespace DazeControl.Core.Runs;

/// <summary>
/// One line of a finished step's "Values" in Runs (T25.5, D-145): a value it gave, or a setting's text after filling.
/// A Sensitive one is <see cref="Hidden"/> and its <see cref="Text"/> is empty: the text itself is never stored (D-014).
/// </summary>
/// <param name="Title">The value's or setting's title in plain words ("Output", "Message").</param>
/// <param name="Text">What it held, masked and cut at <see cref="ShownValues.MaxLength"/>.</param>
/// <param name="Hidden">It is Sensitive (or built from something that is): shown as hidden.</param>
public sealed record ShownValue(string Title, string Text, bool Hidden = false);

/// <summary>Builds what a finished step shows in Runs: its values and its settings' text after filling (T25.5, D-145).</summary>
public static class ShownValues
{
    /// <summary>Each text is cut here, with "…".</summary>
    public const int MaxLength = 256;

    /// <summary>At most this many lines per list, so a step with many settings can't grow a history record without bound.</summary>
    public const int MaxLines = 20;

    /// <summary>
    /// The values <paramref name="step"/> gave (only those a later setting uses are kept by the run, D-129), in the order its
    /// type lists them. Sensitive ones (by type, or marked in this run) are hidden; the rest are masked against
    /// <paramref name="secrets"/> (Sensitive values of earlier steps) before they are cut.
    /// </summary>
    public static ShownValue[]? Values(Flow flow, StepNode step, ProviderDescriptor descriptor, RunValues values, Library library,
        IReadOnlyList<(string Text, string Title)> secrets, IReadOnlyList<ProviderDescriptor> catalog, Func<string, Flow?>? flows)
    {
        var shown = new List<ShownValue>();
        foreach (var spec in StepValues.Of(flow, step, descriptor, flows, catalog))
        {
            if (shown.Count >= MaxLines || !values.TryGet(step.Id, spec.Name, out var given))
                continue;
            var text = StepValues.Show(given, library);
            if (text.Length == 0)
                continue;
            shown.Add(spec.Sensitive || values.IsMarkedSensitive(step.Id, spec.Name)
                ? new ShownValue(spec.Title, "", Hidden: true)
                : new ShownValue(spec.Title, Fit(text, secrets)));
        }
        return shown.Count == 0 ? null : [.. shown];
    }

    /// <summary>
    /// The settings of <paramref name="written"/> whose text changed when its values were filled, as <paramref name="filled"/>
    /// holds them. A setting that mentions a Sensitive value is hidden whole: its filled text may carry the value in a changed
    /// form (percent-encoded in an address), which masking would not find.
    /// </summary>
    public static ShownValue[]? Used(Flow flow, StepNode written, StepNode filled, ProviderDescriptor descriptor, RunValues values,
        IReadOnlyList<(string Text, string Title)> secrets, IReadOnlyList<ProviderDescriptor> catalog, Func<string, Flow?>? flows)
    {
        var shown = new List<ShownValue>();
        foreach (var (key, was) in written.With)
        {
            if (shown.Count >= MaxLines || descriptor.FindParam(key) is not { } spec || !filled.With.TryGetValue(key, out var now) || Equals(was, now))
                continue;
            var texts = was switch
            {
                ScalarParam { Text: var t } => [t],
                ListParam list => list.Items.OfType<ScalarParam>().Select(i => i.Text).ToList(),
                _ => [],
            };
            if (texts.Exists(t => MentionsSecret(flow, t, values, catalog, flows)))
            {
                shown.Add(new ShownValue(spec.Title, "", Hidden: true));
                continue;
            }
            var text = now switch
            {
                ScalarParam { Text: var t } => t,
                ListParam list => string.Join(", ", list.Items.OfType<ScalarParam>().Select(i => i.Text)),
                _ => "",
            };
            if (text.Length > 0)
                shown.Add(new ShownValue(spec.Title, Fit(text, secrets)));
        }
        return shown.Count == 0 ? null : [.. shown];
    }

    /// <summary>Masked first, then cut: a secret cut in half would no longer be found.</summary>
    private static string Fit(string text, IReadOnlyList<(string Text, string Title)> secrets) =>
        RunValues.Cut(StepValues.Mask(text, secrets) ?? "", MaxLength);

    private static bool MentionsSecret(Flow flow, string text, RunValues values, IReadOnlyList<ProviderDescriptor> catalog, Func<string, Flow?>? flows)
    {
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
