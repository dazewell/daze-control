using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>
/// The "Only if" row and the words of check steps (D-081, DESIGN B6). The row's choices come from the step type's
/// <see cref="ProviderDescriptor.Conditions"/>, so providers add checks without touching the editor.
/// </summary>
public sealed partial class InspectorViewModel
{
    public const string AlwaysText = "Always (default)";

    private ValueList<ConditionSpec> _conditions = [];

    /// <summary>The step type has conditions: the row shows.</summary>
    [ObservableProperty]
    public partial bool HasOnlyIf { get; private set; }

    /// <summary>"Always (default)" then one entry per condition, in the user's words.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> OnlyIfOptions { get; private set; } = [];

    /// <summary>The step sets its own "Only if" (else it follows its type's default, T14.10).</summary>
    [ObservableProperty]
    public partial bool OnlyIfIsSet { get; private set; }

    [ObservableProperty]
    public partial string ResetOnlyIfTip { get; private set; } = "";

    /// <summary>0 = Always.</summary>
    [ObservableProperty]
    public partial int OnlyIfIndex { get; set; }

    /// <summary>A check step (<c>area.check</c>): it fails when the answer isn't what the user chose.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicySectionTitle), nameof(FallbackText), nameof(FallbackTip))]
    public partial bool IsCheck { get; private set; }

    /// <summary>"If it isn't" for a check, else "If it fails".</summary>
    public string PolicySectionTitle => IsCheck ? "If it isn't" : "If it fails";

    /// <summary>The fallback menu's button.</summary>
    public string FallbackText => IsCheck ? "If it isn't, run…" : "If it fails, run…";

    public string FallbackTip => IsCheck ? "Add a step that runs only if the answer is no" : "Add a step that runs only if this one fails";


    private void LoadOnlyIf(StepNode step, ProviderDescriptor? descriptor)
    {
        _conditions = descriptor?.Conditions ?? [];
        HasOnlyIf = _conditions.Count > 0;
        var inherited = TypeDefault(step.Step)?.Only;
        var overrides = inherited is not null && inherited != StepNode.OnlyAlways;
        var options = OnlyChoices(_conditions, overrides);
        if (!options.SequenceEqual(OnlyIfOptions)) // a new list resets the open ComboBox's selection, even with the same rows
            OnlyIfOptions = options;
        OnlyIfIndex = OnlyIndex(_conditions, step.Only ?? inherited);
        OnPropertyChanged(nameof(OnlyIfIndex)); // read again: the box may have dropped its selection meanwhile
        OnlyIfIsSet = step.Only is not null;
        ResetOnlyIfTip = $"Use the default ({OnlyIfOptions[OnlyIndex(_conditions, inherited)].ToLowerInvariant()})";
        IsCheck = Outcomes.IsCheck(step.Step);
    }

    private void ClearOnlyIf()
    {
        _conditions = [];
        HasOnlyIf = false;
        IsCheck = false;
        OnlyIfOptions = [];
        OnlyIfIndex = 0;
        OnlyIfIsSet = false;
    }

    partial void OnOnlyIfIndexChanged(int value)
    {
        if (_loading || Node is not StepNode || value < 0 || value > _conditions.Count)
            return;
        // "Always" is the same as no key, unless the type's default has an "only" (T14.10) that it must override.
        var only = OnlyAt(_conditions, value);
        EditStep(s =>
        {
            var inherited = TypeDefault(s.Step)?.Only;
            if (only is null)
                return s with { Only = inherited is not null && inherited != StepNode.OnlyAlways ? StepNode.OnlyAlways : null };
            return s with { Only = only == inherited ? null : only }; // the default's own choice is "not set"
        });
    }

    [RelayCommand]
    private void ResetOnlyIf() => EditStep(s => s with { Only = null });

    /// <summary>"Always (default)" then each condition's label.</summary>
    public static IReadOnlyList<string> OnlyChoices(IReadOnlyList<ConditionSpec> conditions, bool alwaysOverrides = false) =>
        [alwaysOverrides ? "Always" : AlwaysText, .. conditions.Select(c => c.Label)];

    /// <summary>The choice for a stored <c>only</c>: 0 for absent, <c>always</c> or an id the type doesn't know.</summary>
    public static int OnlyIndex(IReadOnlyList<ConditionSpec> conditions, string? only)
    {
        for (var i = 0; i < conditions.Count && only is not null; i++)
            if (conditions[i].Id == only)
                return i + 1;
        return 0;
    }

    /// <summary>What to store for a choice: null (no key) for Always, else the condition's id (B6).</summary>
    public static string? OnlyAt(IReadOnlyList<ConditionSpec> conditions, int index) =>
        index <= 0 || index > conditions.Count ? null : conditions[index - 1].Id;

    /// <summary>"Test this step" on a check says yes or no (a normal answer), on a skipped step what it would skip.</summary>
    public static string ExplainTest(string stepId, NodeResult result)
    {
        ArgumentNullException.ThrowIfNull(stepId);
        ArgumentNullException.ThrowIfNull(result);
        if (result.SkippedBecause is { Length: > 0 } why)
            return "Skipped: " + why;
        if (!Outcomes.IsCheck(stepId))
            return Explain(result);
        return result.Outcome switch
        {
            NodeOutcome.Succeeded => "Yes: " + (result.Summary is { Length: > 0 } yes ? yes : "it is"),
            NodeOutcome.Cancelled => "Test stopped.",
            _ => "No: " + FirstLine(result.Error ?? "it isn't"),
        };
    }
}
