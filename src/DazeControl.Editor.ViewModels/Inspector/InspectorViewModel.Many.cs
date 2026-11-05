using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>What the caption and Reset link of one Many-mode row say (DESIGN D3, D4).</summary>
public sealed record BulkRowView(string Caption, bool ShowReset, string ResetTip, string Name, bool IsDim)
{
    public static readonly BulkRowView None = new("", false, "", "", false);

    public bool ShowCaption => Caption.Length > 0;
}

/// <summary>
/// The Many-mode inspector (T15.2, D-084): the settings the selected steps share (If it fails, Timing, Only if),
/// with differing values shown as mixed, on <see cref="BulkEdit"/>. A change goes to every selected step as one Undo.
/// </summary>
public sealed partial class InspectorViewModel
{
    private BulkEdit? _bulk;

    /// <summary>"3 steps selected · Set default device".</summary>
    [ObservableProperty]
    public partial string ManyHeader { get; private set; } = "";

    [ObservableProperty]
    public partial bool ManyHasTiming { get; private set; }

    [ObservableProperty]
    public partial bool ManyHasOnlyIf { get; private set; }

    [ObservableProperty]
    public partial string ManyPolicyTitle { get; private set; } = "If it fails";

    /// <summary>-1 when the steps differ: no radio is chosen.</summary>
    [ObservableProperty]
    public partial int ManyPolicyIndex { get; set; } = -1;

    [ObservableProperty]
    public partial BulkRowView ManyPolicyView { get; private set; } = BulkRowView.None;

    /// <summary>NaN when the steps differ: the box shows its "Mixed" placeholder.</summary>
    [ObservableProperty]
    public partial double ManyRetries { get; set; } = double.NaN;

    [ObservableProperty]
    public partial BulkRowView ManyRetriesView { get; private set; } = BulkRowView.None;

    [ObservableProperty]
    public partial double ManyRetryDelay { get; set; } = double.NaN;

    [ObservableProperty]
    public partial BulkRowView ManyRetryDelayView { get; private set; } = BulkRowView.None;

    [ObservableProperty]
    public partial bool ManyCanRetryDelay { get; private set; }

    [ObservableProperty]
    public partial double ManyTimeout { get; set; } = double.NaN;

    [ObservableProperty]
    public partial BulkRowView ManyTimeoutView { get; private set; } = BulkRowView.None;

    [ObservableProperty]
    public partial IReadOnlyList<string> ManyOnlyOptions { get; private set; } = [];

    /// <summary>-1 when the steps differ.</summary>
    [ObservableProperty]
    public partial int ManyOnlyIndex { get; set; } = -1;

    [ObservableProperty]
    public partial BulkRowView ManyOnlyView { get; private set; } = BulkRowView.None;

    /// <summary>Why the last change was refused (nothing was applied), else empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManyProblem))]
    public partial string ManyProblem { get; private set; } = "";

    public bool HasManyProblem => ManyProblem.Length > 0;

    private void LoadMany(IEnumerable<string> selected)
    {
        var flow = Flow;
        var nodes = flow is null ? [] : selected.Select(flow.FindNode).OfType<Node>().ToList();
        if (nodes.Count < 2)
        {
            _bulk = null;
            return;
        }
        var bulk = _bulk = new BulkEdit(nodes, TypeDefault);
        ManyHeader = ManyHeaderText(bulk);
        ManyHasTiming = bulk.HasTiming;
        IsCheck = bulk.StepType is { } type && Outcomes.IsCheck(type);
        ManyPolicyTitle = PolicySectionTitle;
        var n = nodes.Count;
        var what = bulk.CardCount == 0 ? "steps" : "items";

        var policy = bulk.Row(BulkField.OnFailure);
        ManyPolicyIndex = policy is { State: not BulkState.Mixed, Value: FailurePolicy p } ? (int)p : -1;
        ManyPolicyView = View(policy, ManyPolicyTitle, v => StepDefaultRow.Policy((FailurePolicy)v!).ToLowerInvariant(), n, what);

        var retries = bulk.Row(BulkField.Retries);
        ManyRetries = retries is { State: not BulkState.Mixed, Value: int r } ? r : double.NaN;
        ManyRetriesView = View(retries, "Try again", v => Times((int)v!), n, what);
        var delay = bulk.Row(BulkField.RetryDelay);
        ManyRetryDelay = delay is { State: not BulkState.Mixed, Value: TimeSpan d } ? d.TotalSeconds : double.NaN;
        ManyRetryDelayView = View(delay, "Wait between tries", v => Seconds((TimeSpan)v!), n, what);
        // Nothing to wait between when no step tries again.
        ManyCanRetryDelay = retries is not { State: not BulkState.Mixed, Value: 0 };
        var timeout = bulk.Row(BulkField.Timeout);
        ManyTimeout = timeout is { State: not BulkState.Mixed, Value: TimeSpan t } ? t.TotalSeconds : double.NaN;
        ManyTimeoutView = View(timeout, "Give up after", v => Seconds((TimeSpan)v!), n, what);

        var only = bulk.Row(BulkField.Only);
        ManyHasOnlyIf = only is not null;
        if (only is not null)
        {
            var inherited = bulk.StepType is { } stepType ? TypeDefault(stepType)?.Only : null;
            var options = OnlyChoices(bulk.OnlyChoices, inherited is not null && inherited != StepNode.OnlyAlways);
            ManyOnlyOptions = options;
            ManyOnlyIndex = only is { State: not BulkState.Mixed, Value: string o } ? OnlyIndex(bulk.OnlyChoices, o) : -1;
            ManyOnlyView = View(only, "Only if", v => options[OnlyIndex(bulk.OnlyChoices, (string?)v)].ToLowerInvariant(), n, what);
        }
        else
        {
            ManyOnlyOptions = [];
            ManyOnlyIndex = -1;
            ManyOnlyView = BulkRowView.None;
        }
    }

    private static string ManyHeaderText(BulkEdit bulk)
    {
        var text = bulk.CardCount == 0 ? Plural(bulk.StepCount, "step") + " selected"
            : bulk.StepCount == 0 ? Plural(bulk.CardCount, "card") + " selected"
            : $"{Plural(bulk.StepCount, "step")} and {Plural(bulk.CardCount, "card")} selected";
        return bulk.Descriptor is { } d ? $"{text} · {d.Title}" : text;
    }

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    /// <summary>Caption, Reset and accessible name of a row: "Give up after, mixed", "Give up after, 10 s, default".</summary>
    private static BulkRowView View(BulkRow? row, string title, Func<object?, string> say, int count, string what)
    {
        if (row is null)
            return BulkRowView.None;
        var tip = $"Use the default for all {count} {what}";
        return row.State switch
        {
            BulkState.Mixed => new BulkRowView("Mixed", row.AnySet, tip, $"{title}, mixed", true),
            BulkState.Default => new BulkRowView("Default", false, tip, $"{title}, {say(row.Value)}, default", true),
            _ => new BulkRowView("", true, tip, $"{title}, {say(row.Value)}", false),
        };
    }

    private void ApplyMany(BulkField field, object? value, bool reset = false)
    {
        if (_loading || _bulk is not { } bulk || !CanEdit)
            return;
        var result = BulkEdit.Apply(_session, _canvas, reset ? bulk.Reset(field) : bulk.Set(field, value!));
        ManyProblem = result.Problems.Count > 0 ? result.Problems[0].Message : "";
        if (!result.Applied)
            Load(); // put the controls back to what the steps still say
    }

    partial void OnManyPolicyIndexChanged(int value)
    {
        if (value is >= 0 and <= 2)
            ApplyMany(BulkField.OnFailure, (FailurePolicy)value);
    }

    partial void OnManyRetriesChanged(double value)
    {
        if (!double.IsNaN(value))
            ApplyMany(BulkField.Retries, (int)Math.Clamp(Math.Round(value), 0, MaxRetries));
    }

    partial void OnManyRetryDelayChanged(double value)
    {
        if (!double.IsNaN(value))
            ApplyMany(BulkField.RetryDelay, TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, value) * 1000)));
    }

    partial void OnManyTimeoutChanged(double value)
    {
        if (!double.IsNaN(value) && value > 0)
            ApplyMany(BulkField.Timeout, TimeSpan.FromMilliseconds(Math.Round(value * 1000)));
    }

    partial void OnManyOnlyIndexChanged(int value)
    {
        if (_bulk is { } bulk && value >= 0 && value <= bulk.OnlyChoices.Count)
            ApplyMany(BulkField.Only, OnlyAt(bulk.OnlyChoices, value) ?? StepNode.OnlyAlways);
    }

    [RelayCommand]
    private void ResetManyPolicy() => ApplyMany(BulkField.OnFailure, null, reset: true);

    [RelayCommand]
    private void ResetManyRetries() => ApplyMany(BulkField.Retries, null, reset: true);

    [RelayCommand]
    private void ResetManyRetryDelay() => ApplyMany(BulkField.RetryDelay, null, reset: true);

    [RelayCommand]
    private void ResetManyTimeout() => ApplyMany(BulkField.Timeout, null, reset: true);

    [RelayCommand]
    private void ResetManyOnly() => ApplyMany(BulkField.Only, null, reset: true);
}
