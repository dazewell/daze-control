using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>
/// One piece of the "Shows:" row under a setting that takes values (D-129, DESIGN §5.3): plain text, or a value as a pill.
/// <see cref="Index"/> counts the references of the stored text, so a pill finds itself again after the text changed.
/// </summary>
public sealed class PillSegment
{
    internal PillSegment(ParamFieldViewModel owner, string text) => (Owner, Text) = (owner, text);

    internal PillSegment(ParamFieldViewModel owner, string text, ValueRef reference, int index, string automationName, string? warning)
    {
        Owner = owner;
        Text = text;
        Ref = reference;
        Index = index;
        AutomationName = automationName;
        Warning = warning;
    }

    public ParamFieldViewModel Owner { get; }

    /// <summary>"Device · Connect Technics", or the plain words.</summary>
    public string Text { get; }

    public ValueRef? Ref { get; }

    public int Index { get; } = -1;

    public bool IsPill => Ref is not null;

    /// <summary>The reference names a value of this flow, so it can be changed and given words for when it is empty.</summary>
    public bool Words { get; internal init; }

    public bool IsPlain => Ref is null;

    /// <summary>"Device from Connect Technics, in Message. Press to change or remove."</summary>
    public string AutomationName { get; } = "";

    /// <summary>Why the value may be empty ("It may not have run yet."), else null; the pill wears a warning sign and this as its tip.</summary>
    public string? Warning { get; }

    public bool HasWarning => Warning is not null;

    /// <summary>The words after "If it has no value, show": what is written after the bar now.</summary>
    public string Fallback => Ref?.Fallback ?? "";
}

public sealed partial class ParamFieldViewModel
{
    /// <summary>The caption for a box whose display token names nothing.</summary>
    public const string UnknownValueText = "Not a value from this flow";

    private IReadOnlyList<PillSegment> _pills = [];

    /// <summary>The row's pieces ("Connected to [Device · Connect Technics] at [Time]"); empty while no value is used.</summary>
    public IReadOnlyList<PillSegment> Pills
    {
        get => _pills;
        private set
        {
            _pills = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPills));
            OnPropertyChanged(nameof(ShowsHelp));
            OnPropertyChanged(nameof(PillsName));
        }
    }

    public bool HasPills => _pills.Count > 0;

    /// <summary>The help line below a setting that takes no values; with values it lives in the pills row instead.</summary>
    public bool ShowsPlainHelp => HasHelp && !TakesValues;

    /// <summary>The row has something to say: pills, or "Not a value from this flow".</summary>
    public bool ShowsRow => HasPills || HasUnknownValue;

    /// <summary>With no value to show the row holds the setting's help (or stays empty), so the slot never changes height (D-104).</summary>
    public bool ShowsHelp => !ShowsRow && HasHelp;

    /// <summary>"Not a value from this flow" under the box while a typed token names nothing.</summary>
    [ObservableProperty]
    public partial bool HasUnknownValue { get; private set; }

    /// <summary>The row as one name for Narrator: "Shows: Connected to Device from Connect Technics".</summary>
    public string PillsName => HasPills
        ? "Shows: " + string.Join("", _pills.Select(p => p.IsPill ? p.Ref is { } r && ValueText.Words(_values!.Value.Flow, r, _catalog) is { } w ? $"{w.Title} from {w.From}" : p.Text : p.Text))
        : "";

    private void RefreshPills()
    {
        if (_values is not { } c || !TakesValues)
        {
            if (_pills.Count > 0)
                Pills = [];
            HasUnknownValue = false;
            OnPropertyChanged(nameof(ShowsRow));
            OnPropertyChanged(nameof(ShowsHelp));
            return;
        }
        var stored = Stored(Text);
        var spec = Spec;
        var problems = StepValues.Problems(c.Flow, c.StepId, spec, stored, _catalog);
        var segments = new List<PillSegment>();
        var index = 0;
        foreach (var part in StepValues.Split(stored))
        {
            if (part.Ref is not { } r)
            {
                segments.Add(new(this, part.Text));
                continue;
            }
            var words = ValueText.Words(c.Flow, r, _catalog);
            var label = words is { } w ? $"{w.Title} · {w.From}" : r.ToString();
            var name = words is { } n ? $"{n.Title} from {n.From}, in {Title}. Press to change or remove." : $"{r}, in {Title}. Not available in this flow. Press to remove.";
            var warning = problems.Where(p => p.Ref.Start == r.Start).Select(p => p.Message).FirstOrDefault()
                ?? (words is null ? "This value isn't available in this flow." : null);
            segments.Add(new(this, label, r, index++, name, warning) { Words = words is not null });
        }
        if (!segments.Any(s => s.IsPill))
            segments.Clear();
        Pills = segments;
        HasUnknownValue = ValueText.HasUnknownValue(c.Flow, Text, _catalog);
        OnPropertyChanged(nameof(ShowsRow));
        OnPropertyChanged(nameof(ShowsHelp));
    }

    /// <summary>Swaps the pill's reference for <paramref name="stored"/> (empty removes it). One write, so one undo step.</summary>
    private void ReplacePill(PillSegment pill, string stored)
    {
        if (_values is not { } c)
            return;
        var text = Stored(Text);
        var refs = StepValues.Parse(text);
        if (pill.Index < 0 || pill.Index >= refs.Count)
            return;
        var r = refs[pill.Index];
        var next = string.Concat(text.AsSpan(0, r.Start), stored, text.AsSpan(r.Start + r.Length));
        Text = ValueText.ToDisplay(c.Flow, next, _catalog);
    }

    /// <summary>"Remove" and Delete on a focused pill.</summary>
    public void RemovePill(PillSegment pill) => ReplacePill(pill, "");

    /// <summary>The pill's reference as the text holds it now: the pill may be older than the last change (a flyout outlives the rebuild).</summary>
    private ValueRef? Current(PillSegment pill)
    {
        var refs = StepValues.Parse(Stored(Text));
        return pill.Index >= 0 && pill.Index < refs.Count ? refs[pill.Index] : null;
    }

    /// <summary>"Change value…": the same reference slot, another value (the fallback written now is kept).</summary>
    public void ChangePill(PillSegment pill, ValueChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var stored = choice.Stored;
        if (Current(pill)?.Fallback is { } fallback && StepValues.Parse(stored) is [{ } only])
            stored = (only with { Fallback = fallback }).ToString();
        ReplacePill(pill, stored);
    }

    /// <summary>"If it has no value, show": the words after the bar; empty takes them away. Braces and line breaks can't be part of it.</summary>
    public void SetPillFallback(PillSegment pill, string? words)
    {
        if (Current(pill) is not { } r)
            return;
        var clean = string.Concat((words ?? "").Where(ch => ch is not ('{' or '}' or (char)13 or (char)10))).Trim();
        ReplacePill(pill, (r with { Fallback = clean.Length > 0 ? clean : null }).ToString());
    }
}
