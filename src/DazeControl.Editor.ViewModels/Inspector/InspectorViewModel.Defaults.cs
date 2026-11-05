using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>
/// The Timing and "If it fails" rows of a step as Default / Set (D-080, DESIGN A5): each row shows the effective value,
/// dimmer when the step inherits it, with a Reset that clears only that field; "Use these for all … steps…" writes the
/// effective values to <c>settings.stepDefaults</c>.
/// </summary>
public sealed partial class InspectorViewModel
{
    private StepDefaultsEditor? _defaults;
    private string? _noticeNode;
    private StepDefault? _previousDefault;
    private string? _previousStepId;

    [ObservableProperty]
    public partial bool TimeoutIsSet { get; private set; }

    [ObservableProperty]
    public partial bool RetriesIsSet { get; private set; }

    [ObservableProperty]
    public partial bool RetryDelayIsSet { get; private set; }

    [ObservableProperty]
    public partial bool PolicyIsSet { get; private set; }

    /// <summary>"Use the default (1 s)": the Reset link's tooltip.</summary>
    [ObservableProperty]
    public partial string ResetTimeoutTip { get; private set; } = "";

    [ObservableProperty]
    public partial string ResetRetriesTip { get; private set; } = "";

    [ObservableProperty]
    public partial string ResetRetryDelayTip { get; private set; } = "";

    [ObservableProperty]
    public partial string ResetPolicyTip { get; private set; } = "";

    /// <summary>"From the default for Set default device steps" when the type has an entry, else empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultsCaption))]
    public partial string DefaultsCaption { get; private set; } = "";

    public bool HasDefaultsCaption => DefaultsCaption.Length > 0;

    /// <summary>"Use these for all Set default device steps…"</summary>
    [ObservableProperty]
    public partial string UseForAllText { get; private set; } = "";

    /// <summary>The values the flyout lists, one per line.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> UseForAllValues { get; private set; } = [];

    /// <summary>"Applies to 3 other steps in 2 flows that don't set their own".</summary>
    [ObservableProperty]
    public partial string UseForAllApplies { get; private set; } = "";

    /// <summary>"Saved as the default for Set default device steps" after Apply; empty when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultsNotice))]
    public partial string DefaultsNotice { get; private set; } = "";

    public bool HasDefaultsNotice => DefaultsNotice.Length > 0;

    private StepDefault? TypeDefault(string stepId) =>
        _session.Current.Settings.StepDefaults.TryGetValue(stepId, out var d) ? d : null;

    private void LoadTiming(StepNode step, ProviderDescriptor? descriptor)
    {
        var typeDefault = TypeDefault(step.Step);
        var effective = StepSettings.Resolve(step, descriptor, typeDefault);
        PolicyIndex = (int)effective.OnFailure;
        Retries = effective.RetryCount;
        RetryDelay = effective.RetryDelay.TotalSeconds;
        Timeout = effective.Timeout.TotalSeconds;
        TimeoutPlaceholder = descriptor is null ? "" : Seconds(descriptor.DefaultTimeout);

        TimeoutIsSet = step.Timeout is not null;
        RetriesIsSet = step.RetryCount is not null;
        RetryDelayIsSet = step.RetryDelay is not null;
        PolicyIsSet = step.OnFailure is not null;
        var inherited = StepSettings.Resolve(step with { Timeout = null, RetryCount = null, RetryDelay = null, OnFailure = null }, descriptor, typeDefault);
        ResetTimeoutTip = $"Use the default ({Seconds(inherited.Timeout)})";
        ResetRetriesTip = $"Use the default ({Times(inherited.RetryCount)})";
        ResetRetryDelayTip = $"Use the default ({Seconds(inherited.RetryDelay)})";
        ResetPolicyTip = $"Use the default ({StepDefaultRow.Policy(inherited.OnFailure).ToLowerInvariant()})";

        var title = descriptor?.Title ?? step.Step;
        DefaultsCaption = typeDefault is { IsEmpty: false } ? $"From the default for {title} steps" : "";
        UseForAllText = $"Use these for all {title} steps…";
        UseForAllValues =
        [
            $"Give up after {Seconds(effective.Timeout)}",
            effective.RetryCount > 0
                ? $"Try again {Times(effective.RetryCount)}, wait {Seconds(effective.RetryDelay)} between tries"
                : "Don't try again",
            StepDefaultRow.Policy(effective.OnFailure),
        ];
        if (descriptor?.Conditions.Count > 0 && effective.Only is { } only && only != StepNode.OnlyAlways)
            UseForAllValues = [.. UseForAllValues, "Only if " + CanvasNodeViewModel.OnlyLabel(step.Step, only)];
        UseForAllApplies = Applies(step);

        if (_noticeNode != step.Id)
            DefaultsNotice = "";
    }

    private static string Times(int count) => count == 0 ? "0 times" : count == 1 ? "1 time" : $"{count} times";

    /// <summary>Other steps of this type that leave at least one of the four fields to the default: they follow it.</summary>
    private string Applies(StepNode self)
    {
        var steps = 0;
        var flows = 0;
        foreach (var flow in _session.Current.Flows)
        {
            var n = flow.Nodes.OfType<StepNode>().Count(s => s.Step == self.Step && !(flow.Id == FlowId && s.Id == self.Id)
                && (s.Timeout is null || s.RetryCount is null || s.RetryDelay is null || s.OnFailure is null
                    || s.Only is null && ProviderCatalog.Find(s.Step)?.Conditions.Count > 0));
            steps += n;
            flows += n > 0 ? 1 : 0;
        }
        return steps == 0
            ? "No other steps follow it: they all set their own."
            : $"Applies to {steps} other {(steps == 1 ? "step" : "steps")} in {flows} {(flows == 1 ? "flow" : "flows")} that don't set their own.";
    }

    [RelayCommand]
    private void ResetTimeout() => EditStep(s => s with { Timeout = null });

    [RelayCommand]
    private void ResetRetries() => EditStep(s => s with { RetryCount = null });

    [RelayCommand]
    private void ResetRetryDelay() => EditStep(s => s with { RetryDelay = null });

    [RelayCommand]
    private void ResetPolicy() => EditStep(s => s with { OnFailure = null });

    /// <summary>
    /// "Use these for all … steps…" → Apply: the step's effective values become the type's default. Steps keep what
    /// they set; the ones that inherit follow. Undo restores the previous entry.
    /// </summary>
    [RelayCommand]
    private void ApplyUseForAll()
    {
        if (Node is not StepNode step || !CanEdit || ProviderCatalog.Find(step.Step) is null)
            return;
        _defaults ??= new StepDefaultsEditor(_session);
        var effective = StepSettings.Resolve(step, ProviderCatalog.Find(step.Step), TypeDefault(step.Step));
        _previousStepId = step.Step;
        _previousDefault = TypeDefault(step.Step);
        _defaults.Set(step.Step, (_previousDefault ?? new StepDefault()) with
        {
            Timeout = effective.Timeout,
            RetryCount = effective.RetryCount,
            RetryDelay = effective.RetryCount > 0 ? effective.RetryDelay : _previousDefault?.RetryDelay,
            OnFailure = effective.OnFailure,
            // "Always" with no condition default is the built-in: no key (B8).
            Only = effective.Only == StepNode.OnlyAlways && _previousDefault?.Only is null ? null : effective.Only,
        });
        _noticeNode = step.Id;
        DefaultsNotice = $"Saved as the default for {ProviderCatalog.Find(step.Step)?.Title ?? step.Step} steps";
    }

    [RelayCommand]
    private void UndoDefaults()
    {
        if (_previousStepId is { } id && CanEdit)
        {
            _defaults ??= new StepDefaultsEditor(_session);
            _defaults.Set(id, _previousDefault);
        }
        DismissDefaultsNotice();
    }

    [RelayCommand]
    private void DismissDefaultsNotice()
    {
        _previousStepId = null;
        _previousDefault = null;
        _noticeNode = null;
        DefaultsNotice = "";
    }
}
