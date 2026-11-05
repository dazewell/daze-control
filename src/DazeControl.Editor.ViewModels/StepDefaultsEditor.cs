using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Runs;

namespace DazeControl.Editor.ViewModels;

/// <summary>
/// The "Step defaults" card (D-080, DESIGN Part A5): one row per step type with an entry in
/// <c>settings.stepDefaults</c>. Edits go through the session, so autosave writes that key and nothing else.
/// The inspector's "Use these for all … steps" (T14.4) reuses <see cref="Set"/> and <see cref="Usage"/>.
/// </summary>
public sealed class StepDefaultsEditor : IDisposable
{
    public const double MaxTimeoutSeconds = 24 * 3600;
    public const int MaxRetryCount = 10;
    public const double MaxDelaySeconds = 3600;

    private readonly ConfigSession _session;

    public StepDefaultsEditor(ConfigSession session)
    {
        _session = session;
        _session.PropertyChanged += OnSessionChanged;
        Refresh();
    }

    public ObservableCollection<StepDefaultRow> Rows { get; } = [];

    /// <summary>Step types without an entry yet, in palette order.</summary>
    public ObservableCollection<PaletteItem> Addable { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>"Used by n steps in m flows" counts for a step type, as the config stands now.</summary>
    public (int Steps, int Flows) Usage(string stepId)
    {
        var steps = 0;
        var flows = 0;
        foreach (var flow in _session.Current.Flows)
        {
            var n = flow.Nodes.OfType<StepNode>().Count(s => s.Step == stepId);
            steps += n;
            flows += n > 0 ? 1 : 0;
        }
        return (steps, flows);
    }

    /// <summary>Adds an entry for a type that has none, starting from its built-in timeout so it isn't empty.</summary>
    public void Add(string stepId)
    {
        var descriptor = ProviderCatalog.All.FirstOrDefault(d => d.Id == stepId);
        if (descriptor is null || _session.Current.Settings.StepDefaults.ContainsKey(stepId))
            return;
        Set(stepId, new StepDefault { Timeout = descriptor.DefaultTimeout });
    }

    public void Remove(string stepId) => Set(stepId, null);

    /// <summary>Replaces one type's entry; null or an empty entry removes it.</summary>
    public void Set(string stepId, StepDefault? value)
    {
        _session.Update(c =>
        {
            var entries = c.Settings.StepDefaults.Where(e => e.Key != stepId).ToList();
            if (value is { IsEmpty: false })
                entries.Add(new(stepId, value));
            return c with { Settings = c.Settings with { StepDefaults = new(entries) } };
        });
    }

    internal StepDefault Get(string stepId) =>
        _session.Current.Settings.StepDefaults.TryGetValue(stepId, out var d) ? d : new StepDefault();

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfigSession.Current) or nameof(ConfigSession.CanEdit))
            Refresh();
    }

    private void Refresh()
    {
        var entries = _session.Current.Settings.StepDefaults;
        var order = StepPaletteViewModel.All;
        // Rows stay put while their flyout is open: update in place, add and remove the rest.
        foreach (var gone in Rows.Where(r => !entries.ContainsKey(r.StepId)).ToList())
            Rows.Remove(gone);
        foreach (var item in order.Where(i => entries.ContainsKey(i.Step)))
        {
            var row = Rows.FirstOrDefault(r => r.StepId == item.Step);
            if (row is null)
            {
                row = new StepDefaultRow(this, item);
                Rows.Insert(IndexFor(item), row);
            }
            row.Load(entries[item.Step], Usage(item.Step), _session.CanEdit);
        }
        var missing = order.Where(i => !entries.ContainsKey(i.Step)).ToList();
        if (!missing.Select(m => m.Step).SequenceEqual(Addable.Select(a => a.Step)))
        {
            Addable.Clear();
            foreach (var m in missing)
                Addable.Add(m);
        }
    }

    private int IndexFor(PaletteItem item)
    {
        var ids = StepPaletteViewModel.All.Select(i => i.Step).ToList();
        var mine = ids.IndexOf(item.Step);
        return Rows.Count(r => ids.IndexOf(r.StepId) < mine);
    }

    public void Dispose() => _session.PropertyChanged -= OnSessionChanged;
}

/// <summary>One type's defaults: a summary line and the four editable fields of its Edit flyout.</summary>
public sealed partial class StepDefaultRow : ObservableObject
{
    private readonly StepDefaultsEditor _owner;
    private bool _syncing;

    internal StepDefaultRow(StepDefaultsEditor owner, PaletteItem item)
    {
        _owner = owner;
        StepId = item.Step;
        Title = $"{item.Area} › {item.Title}";
        Glyph = item.Glyph;
        var conditions = ProviderCatalog.Find(item.Step)?.Conditions ?? [];
        OnlyOptions = conditions.Count == 0 ? [] : ["Always", .. conditions.Select(c => c.Label)];
    }

    public string StepId { get; }

    public string Title { get; }

    public string Glyph { get; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string UsedBy { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    /// <summary>NaN = not set (the step type's built-in applies).</summary>
    [ObservableProperty]
    public partial double TimeoutSeconds { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double RetryCount { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double RetryDelaySeconds { get; set; } = double.NaN;

    /// <summary>-1 = not set; else <see cref="FailurePolicy"/> order.</summary>
    [ObservableProperty]
    public partial int PolicyIndex { get; set; } = -1;

    /// <summary>"Always" then the type's conditions; empty when the type has none.</summary>
    public IReadOnlyList<string> OnlyOptions { get; }

    public bool HasOnly => OnlyOptions.Count > 0;

    /// <summary>-1 = not set; 0 = Always, written as <c>only: always</c>; then the conditions.</summary>
    [ObservableProperty]
    public partial int OnlyIndex { get; set; } = -1;

    public string EditName => $"Edit defaults for {Title}";

    public string RemoveName => $"Remove defaults for {Title}";

    internal void Load(StepDefault value, (int Steps, int Flows) usage, bool canEdit)
    {
        _syncing = true;
        try
        {
            TimeoutSeconds = value.Timeout?.TotalSeconds ?? double.NaN;
            RetryCount = value.RetryCount ?? double.NaN;
            RetryDelaySeconds = value.RetryDelay?.TotalSeconds ?? double.NaN;
            PolicyIndex = value.OnFailure is { } p ? (int)p : -1;
            OnlyIndex = value.Only is null ? -1 : Inspector.InspectorViewModel.OnlyIndex(ProviderCatalog.Find(StepId)?.Conditions ?? [], value.Only);
            CanEdit = canEdit;
        }
        finally
        {
            _syncing = false;
        }
        Summary = Describe(value, StepId);
        UsedBy = Used(usage);
    }

    public void ResetTimeout() => TimeoutSeconds = double.NaN;

    public void ResetRetryCount() => RetryCount = double.NaN;

    public void ResetRetryDelay() => RetryDelaySeconds = double.NaN;

    public void ResetPolicy() => PolicyIndex = -1;

    public void ResetOnly() => OnlyIndex = -1;

    partial void OnOnlyIndexChanged(int value) => Push(d => d with
    {
        Only = value < 0 || value >= OnlyOptions.Count ? null
            : Inspector.InspectorViewModel.OnlyAt(ProviderCatalog.Find(StepId)?.Conditions ?? [], value) ?? StepNode.OnlyAlways,
    });

    partial void OnTimeoutSecondsChanged(double value) => Push(d => d with { Timeout = ToTime(value, StepDefaultsEditor.MaxTimeoutSeconds, minimum: 0.1) });

    partial void OnRetryCountChanged(double value) => Push(d => d with
    {
        RetryCount = double.IsNaN(value) ? null : (int)Math.Clamp(Math.Round(value), 0, StepDefaultsEditor.MaxRetryCount),
    });

    partial void OnRetryDelaySecondsChanged(double value) => Push(d => d with { RetryDelay = ToTime(value, StepDefaultsEditor.MaxDelaySeconds, minimum: 0) });

    partial void OnPolicyIndexChanged(int value) => Push(d => d with
    {
        OnFailure = Enum.IsDefined(typeof(FailurePolicy), value) ? (FailurePolicy)value : null,
    });

    private void Push(Func<StepDefault, StepDefault> change)
    {
        if (!_syncing)
            _owner.Set(StepId, change(_owner.Get(StepId)));
    }

    private static TimeSpan? ToTime(double seconds, double maximum, double minimum) =>
        double.IsNaN(seconds) ? null : TimeSpan.FromSeconds(Math.Clamp(Math.Round(seconds, 1), minimum, maximum));

    internal static string Policy(FailurePolicy policy) => policy switch
    {
        FailurePolicy.Stop => "Stop the flow",
        FailurePolicy.Skip => "Skip what depends on it",
        _ => "Keep going",
    };

    /// <summary>"Give up after 30 s · Try again 2 times · Skip what depends on it".</summary>
    internal static string Describe(StepDefault value, string? stepId = null)
    {
        var parts = new List<string>();
        if (value.Timeout is { } t)
            parts.Add($"Give up after {RunWords.Seconds(t)}");
        if (value.RetryCount is { } n)
            parts.Add(n == 0 ? "Don't try again" : $"Try again {n} {(n == 1 ? "time" : "times")}");
        if (value.RetryDelay is { } d)
            parts.Add($"Wait {RunWords.Seconds(d)} between tries");
        if (value.OnFailure is { } p)
            parts.Add(Policy(p));
        if (value.Only is { } only && stepId is not null)
            parts.Add(only == StepNode.OnlyAlways ? "Always" : "Only if " + CanvasNodeViewModel.OnlyLabel(stepId, only));
        return string.Join(" · ", parts);
    }

    internal static string Used((int Steps, int Flows) usage) => usage.Steps == 0
        ? "Not used yet"
        : $"Used by {usage.Steps} {(usage.Steps == 1 ? "step" : "steps")} in {usage.Flows} {(usage.Flows == 1 ? "flow" : "flows")}";
}
