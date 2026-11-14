using System.Globalization;
using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Core.Runs;

/// <summary>One value a step gave in a run: text, invariant digits, or a Library key (<see cref="Library"/> = its kind, if known).</summary>
public readonly record struct StepValue(ValueKind Kind, string Text, LibraryKind? Library = null);

/// <summary>
/// The values of one run (D-129, DESIGN §4.5/§4.7): what its steps gave, keyed by step id and value name, plus the flow's
/// inputs and the run values (<c>@flow</c>, <c>@trigger</c>, <c>@time</c>, <c>@date</c>). Lives only while the run does.
/// The runner clears a step's values when an attempt starts and sets them after it; <see cref="StepValues.Fill"/> reads them.
/// Thread-safe: parallel branches write and read.
/// </summary>
public sealed class RunValues
{
    /// <summary>A value is cut here, with "…" (DESIGN §4.4).</summary>
    public const int MaxValueLength = 1000;

    /// <summary>A value a Get from text reads is kept up to here instead (T25.2, D-144).</summary>
    public const int MaxLongValueLength = TextExtract.MaxFilled;

    private readonly Dictionary<(string Step, string Value), StepValue> _values = [];
    private readonly Dictionary<string, StepValue> _inputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sensitiveInputs = new(StringComparer.Ordinal);
    private readonly HashSet<(string Step, string Value)> _sensitive = [];
    private readonly IReadOnlySet<(string Step, string Value)>? _wanted;
    private readonly Lock _lock = new();

    /// <param name="wanted">The values some setting references (<see cref="StepValues.Wanted"/>); others aren't kept. Null = keep all.</param>
    /// <param name="time">For <c>@time</c> and <c>@date</c>; the system clock by default.</param>
    public RunValues(IReadOnlySet<(string Step, string Value)>? wanted = null, TimeProvider? time = null)
    {
        _wanted = wanted;
        Time = time ?? TimeProvider.System;
    }

    /// <summary><c>@flow</c>: the flow's name.</summary>
    public string FlowName { get; init; } = "";

    /// <summary><c>@trigger</c>: what started the run ("Alt+Z", "Schedule · 22:30").</summary>
    public string Trigger { get; init; } = "";

    /// <summary><c>@event</c>: the device, Wi-Fi or cable name that started the run; empty when something else did (T24.6).</summary>
    public string Event { get; init; } = "";

    public TimeProvider Time { get; }

    /// <summary>The values a Get from text reads (<see cref="StepValues.LongValues"/>): kept up to <see cref="MaxLongValueLength"/>. Empty = none.</summary>
    public IReadOnlySet<(string Step, string Value)> LongValues { get; init; } = new HashSet<(string, string)>();

    public bool IsLong(string step, string value) => LongValues.Contains((step, value));

    /// <summary>Whether a setting references this value (so a provider can skip costly work, e.g. reading stdout).</summary>
    public bool Wants(string step, string value) => _wanted is null || _wanted.Contains((step, value));

    /// <summary>A text value, trimmed and cut at <see cref="MaxValueLength"/>.</summary>
    public void SetText(string step, string value, string text) =>
        Set(step, value, new(ValueKind.Text, Cut(text.Trim(), IsLong(step, value) ? MaxLongValueLength : MaxValueLength)));

    public void SetNumber(string step, string value, long number) =>
        Set(step, value, new(ValueKind.Number, number.ToString(CultureInfo.InvariantCulture)));

    public void SetNumber(string step, string value, double number) =>
        Set(step, value, new(ValueKind.Number, number.ToString(CultureInfo.InvariantCulture)));

    /// <summary>A Library key; <paramref name="kind"/> picks the item when the key's kind is known.</summary>
    public void SetDevice(string step, string value, string key, LibraryKind? kind = null) =>
        Set(step, value, new(ValueKind.Device, key.Trim(), kind));

    /// <summary>The step's <c>error</c> (why the attempt failed).</summary>
    public void SetError(string step, string message) => SetText(step, StepValues.ErrorName, message);

    public void Set(string step, string value, StepValue given)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(value);
        if (!Wants(step, value))
            return;
        lock (_lock)
            _values[(step, value)] = given;
    }

    /// <summary>Forgets every value of <paramref name="step"/> (an attempt starts; the last attempt's values stand).</summary>
    public void Clear(string step)
    {
        lock (_lock)
        {
            foreach (var key in _values.Keys.Where(k => k.Step == step).ToList())
                _values.Remove(key);
        }
    }

    public bool TryGet(string step, string value, out StepValue found)
    {
        lock (_lock)
            return _values.TryGetValue((step, value), out found);
    }

    /// <summary>
    /// Marks a value Sensitive in this run although its type doesn't say so (T23.7): a call's output filled from a
    /// Sensitive value of the called run. <see cref="StepValues.Secrets"/> hides it like any other.
    /// </summary>
    public void MarkSensitive(string step, string value)
    {
        lock (_lock)
            _sensitive.Add((step, value));
    }

    /// <summary>Whether <see cref="MarkSensitive"/> marked this value.</summary>
    public bool IsMarkedSensitive(string step, string value)
    {
        lock (_lock)
            return _sensitive.Contains((step, value));
    }

    /// <summary>The flow input <paramref name="name"/> holds <paramref name="key"/> in this run (<c>{$name}</c> shows its name).</summary>
    public void SetInput(string name, LibraryKind kind, string key)
    {
        lock (_lock)
            _inputs[name] = new(ValueKind.Device, key, kind);
    }

    /// <summary>
    /// The text input <paramref name="name"/> holds <paramref name="text"/> in this run (T23.7), trimmed and cut at
    /// <see cref="MaxValueLength"/>. <paramref name="sensitive"/>: the caller filled it from a Sensitive value, so errors and
    /// summaries of this run show its title instead (D-014).
    /// </summary>
    public void SetInputText(string name, string text, bool sensitive = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_lock)
        {
            _inputs[name] = new(ValueKind.Text, Cut(text.Trim()));
            if (sensitive)
                _sensitiveInputs.Add(name);
            else
                _sensitiveInputs.Remove(name);
        }
    }

    /// <summary>The text inputs filled from a Sensitive value, with their text (empty ones left out).</summary>
    public IReadOnlyList<(string Name, string Text)> SensitiveInputs()
    {
        lock (_lock)
        {
            return [.. _sensitiveInputs.Where(n => _inputs.TryGetValue(n, out var v) && v.Text.Length > 0).Select(n => (n, _inputs[n].Text))];
        }
    }

    public bool TryGetInput(string name, out StepValue found)
    {
        lock (_lock)
            return _inputs.TryGetValue(name, out found);
    }

    /// <summary>A run value (<c>@flow</c> → "flow"), or null for an unknown name.</summary>
    public string? Run(string name)
    {
        var now = Time.GetLocalNow();
        return name switch
        {
            StepValues.RunFlow => FlowName,
            StepValues.RunTrigger => Trigger,
            StepValues.RunEvent => Event,
            StepValues.RunTime => now.ToString("HH:mm", CultureInfo.InvariantCulture),
            StepValues.RunDate => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>The Library key of Device value <paramref name="value"/> of <paramref name="step"/>, or null: what <c>$result:step.value</c> becomes (D-130).</summary>
    public string? Device(string step, string value) => TryGet(step, value, out var given) && given.Kind == ValueKind.Device ? given.Text : null;

    /// <summary>Step id → Library key of its <c>device</c> value: what <see cref="PickResults.Resolve"/> swaps into <c>$result:id</c>.</summary>
    public IReadOnlyDictionary<string, string> Picked()
    {
        lock (_lock)
        {
            return _values.Where(e => e.Key.Value == StepValues.DeviceName && e.Value.Kind == ValueKind.Device)
                .ToDictionary(e => e.Key.Step, e => e.Value.Text, StringComparer.Ordinal);
        }
    }

    internal static string Cut(string text, int max = MaxValueLength) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}
