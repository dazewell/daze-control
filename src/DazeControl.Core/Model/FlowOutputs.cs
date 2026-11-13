using System.Text;
using System.Text.RegularExpressions;
using DazeControl.Core.Catalog;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;

namespace DazeControl.Core.Model;

/// <summary>
/// Flow outputs (T23.7, DESIGN §9): <c>outputs: { device: "{bt.device}" }</c> on a flow names text values it gives back.
/// A <c>flow.run</c> that waits gives them to the caller's later steps as <c>{call.device}</c>. Each is filled once, when
/// the called run ends, from that run's values; a missing value shows its fallback, else nothing.
/// </summary>
public static partial class FlowOutputs
{
    /// <summary>A flow gives at most this many outputs.</summary>
    public const int MaxCount = 20;

    /// <summary>An output's written text is at most this long (its filled value is cut at <see cref="RunValues.MaxValueLength"/>).</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Whether <paramref name="step"/> is a <c>flow.run</c> that waits, so it gives the called flow's outputs.</summary>
    public static bool Waits(StepNode step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Step == ProviderCatalog.RunFlowId
            && !(step.With.TryGetValue("wait", out var wait) && wait is ScalarParam { Text: var text } && bool.TryParse(text, out var on) && !on);
    }

    /// <summary>An output's title from its name: "deviceName" → "Device name".</summary>
    public static string Title(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
            return name;
        var title = new StringBuilder(name.Length + 4);
        title.Append(char.ToUpperInvariant(name[0]));
        foreach (var c in name.AsSpan(1))
        {
            if (char.IsAsciiLetterUpper(c))
                title.Append(' ').Append(char.ToLowerInvariant(c));
            else
                title.Append(c);
        }
        return title.ToString();
    }

    /// <summary>Whether <paramref name="name"/> is a value name (<c>[a-z][A-Za-z0-9]*</c>), so <c>{call.name}</c> can reach it.</summary>
    public static bool IsName(string name) => OutputName().IsMatch(name);

    /// <summary>A new output's name ("value", "value2"), unique in the flow.</summary>
    public static string NewName(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var name = "value";
        for (var n = 2; flow.Outputs.ContainsKey(name) || name == StepValues.ErrorName; n++)
            name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"value{n}");
        return name;
    }

    /// <summary>
    /// What is wrong with output <paramref name="name"/>: its name, its size, and its references, checked as if a step ran
    /// after every other step (all have finished when outputs are filled). A value that may be missing warns, as in steps.
    /// </summary>
    public static IReadOnlyList<ValueProblem> Problems(Flow flow, string name, string text, IReadOnlyList<ProviderDescriptor>? catalog = null,
        ValueDictionary<StepDefault>? defaults = null, Func<string, Flow?>? flows = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(text);
        catalog ??= ProviderCatalog.All;
        var problems = new List<ValueProblem>();
        var none = new ValueRef(ValueSource.Run, name, "", null, 0, 0);
        if (!IsName(name) || name == StepValues.ErrorName)
            problems.Add(new(Severity.Error, "Name outputs with letters and digits, starting with a small letter (like device), and not \"error\".", none));
        if (text.Length > MaxTextLength)
            problems.Add(new(Severity.Error, $"An output holds at most {MaxTextLength} characters.", none));
        foreach (var r in StepValues.Parse(text))
        {
            switch (r.Source)
            {
                case ValueSource.Input:
                    if (!flow.Inputs.ContainsKey(r.Name))
                        problems.Add(new(Severity.Error, $"This flow has no input called \"{r.Name}\".", r));
                    continue;
                case ValueSource.Run:
                    if (StepValues.RunTitle(r.Name) is null)
                        problems.Add(new(Severity.Error, $"There's no run value called \"{r.Name}\" ({string.Join(", ", StepValues.RunNames)}).", r));
                    continue;
            }
            if (flow.FindNode(r.Name) is not StepNode producer)
            {
                problems.Add(new(Severity.Warning, $"There's no step called \"{r.Name}\" in this flow, so {r} shows as written.", r));
                continue;
            }
            if (catalog.FirstOrDefault(d => d.Id == producer.Step) is not { } descriptor)
                continue;
            var stepName = StepValues.NameOf(producer, descriptor);
            var value = StepValues.Find(flow, producer, descriptor, r.Value, flows, catalog);
            if (value is null && r.Value != StepValues.ErrorName && Waits(producer))
            {
                problems.Add(new(Severity.Warning, NotGiven(producer, r, flows), r));
                continue;
            }
            if (value is null)
            {
                problems.Add(new(Severity.Error, $"{stepName} gives no value called \"{r.Value}\" (it gives "
                    + string.Join(", ", StepValues.Of(flow, producer, descriptor, flows, catalog).Select(v => v.Title)) + ").", r));
                continue;
            }
            if (r.Fallback is null && MissingAtEnd(flow, producer, value, defaults) is { } missing)
                problems.Add(new(Severity.Warning, $"\"{stepName}\" {missing}, so {value.Title} can be empty. Add what to show when it has no value.", r));
        }
        return problems;
    }

    /// <summary>
    /// Why a value may be missing when the run ends (null = sure). Conservative: the producer must have no "Only if", stop the
    /// flow when it fails (or the value is given on failure too), and be reached from the flow's start by success edges only.
    /// </summary>
    private static string? MissingAtEnd(Flow flow, StepNode producer, ValueSpec value, ValueDictionary<StepDefault>? defaults)
    {
        if (value.Name == StepValues.ErrorName)
            return "gives it only when it fails";
        StepDefault? typeDefault = defaults is { } d && d.TryGetValue(producer.Step, out var found) ? found : null;
        if ((producer.Only ?? typeDefault?.Only) is { } only && only != StepNode.OnlyAlways)
            return "may be skipped (Only if)";
        var unit = FlowShape.UnitOf(flow, producer.Id);
        var ignores = (producer.OnFailure ?? typeDefault?.OnFailure ?? FailurePolicy.Stop) == FailurePolicy.Ignore
            || flow.FindNode(unit) is GroupNode { OnFailure: FailurePolicy.Ignore };
        var withFallback = FlowShape.WithFallback(flow);
        if (!value.OnFailure && (ignores || withFallback.Contains(unit)))
            return "may fail and the flow carry on";
        var fallbacks = FlowShape.Fallbacks(flow);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Sure(string id)
        {
            if (!seen.Add(id) || fallbacks.Contains(id) || flow.FindNode(id) is not { } node)
                return false;
            return node.After.All(e => e.When == EdgeWhen.Success && !withFallback.Contains(e.From) && Sure(e.From));
        }
        return Sure(unit) ? null : "may not run";
    }

    /// <summary>
    /// The outputs filled from <paramref name="values"/> at the end of a run: name → text (cut at <see cref="RunValues.MaxValueLength"/>).
    /// A reference to a step that isn't in the flow stays as written; a missing value shows its fallback, else nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Fill(Flow flow, RunValues values, Library library)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(library);
        var filled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, text) in flow.Outputs)
            filled[name] = RunValues.Cut(StepValues.FillText(flow, text, values, library, singleLine: false).Trim());
        return filled;
    }

    /// <summary>
    /// Renaming output <paramref name="oldName"/> of flow <paramref name="flowId"/>: the output itself and every
    /// <c>{call.oldName}</c> in flows whose <c>flow.run</c> waits for it. Refused (same config) when the new name isn't a value
    /// name or is taken.
    /// </summary>
    public static Configuration Rename(Configuration config, string flowId, string oldName, string newName,
        IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Flows.FirstOrDefault(f => f.Id == flowId) is not { } owner || !owner.Outputs.ContainsKey(oldName)
            || oldName == newName || !IsName(newName) || newName == StepValues.ErrorName || owner.Outputs.ContainsKey(newName))
            return config;
        var flows = config.Flows.Select(f =>
        {
            if (f.Id == flowId)
                f = f with { Outputs = new(f.Outputs.Select(o => o.Key == oldName ? KeyValuePair.Create(newName, o.Value) : o)) };
            string? Renamed(ValueRef r) => Calls(f, r, flowId, oldName) ? (r with { Value = newName }).ToString() : null;
            return StepValues.RewriteRefs(f, (_, r) => Renamed(r), catalog, Renamed);
        });
        return config with { Flows = [.. flows] };
    }

    /// <summary>
    /// Removing output <paramref name="name"/> of flow <paramref name="flowId"/>: the callers' <c>{call.name}</c> become their
    /// fallback text (nothing when none was written), as when a step is deleted.
    /// </summary>
    public static Configuration Remove(Configuration config, string flowId, string name, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Flows.FirstOrDefault(f => f.Id == flowId) is not { } owner || !owner.Outputs.ContainsKey(name))
            return config;
        var flows = config.Flows.Select(f =>
        {
            if (f.Id == flowId)
                f = f with { Outputs = new(f.Outputs.Where(o => o.Key != name)) };
            string? Dropped(ValueRef r) => Calls(f, r, flowId, name) ? StepValues.Escape(r.Fallback ?? "") : null;
            return StepValues.RewriteRefs(f, (_, r) => Dropped(r), catalog, Dropped);
        });
        return config with { Flows = [.. flows] };
    }

    /// <summary>"Headphones doesn't give a value called "device", so {call.device} shows nothing." (T23.7: a warning, never an error.)</summary>
    internal static string NotGiven(StepNode call, ValueRef r, Func<string, Flow?>? flows)
    {
        var target = ConfigValidator.CalledFlow(call) ?? "";
        var name = flows?.Invoke(target)?.Name ?? target;
        var shows = r.Fallback is { Length: > 0 } fallback ? $"\"{fallback}\"" : "nothing";
        return $"{name} doesn't give a value called \"{r.Value}\", so {r} shows {shows}. Add it to {name}'s values, or pick one it gives.";
    }

    /// <summary>Whether <paramref name="r"/> in <paramref name="flow"/> reads output <paramref name="output"/> of a waiting call of <paramref name="flowId"/>.</summary>
    private static bool Calls(Flow flow, ValueRef r, string flowId, string output) =>
        r.Source == ValueSource.Step && r.Value == output && flow.FindNode(r.Name) is StepNode producer && Waits(producer)
        && ConfigValidator.CalledFlow(producer) == flowId;

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex OutputName();
}
