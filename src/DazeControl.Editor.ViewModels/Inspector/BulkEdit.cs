using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>The settings several selected steps can share (D-084, DESIGN Part D).</summary>
public enum BulkField
{
    OnFailure,
    Timeout,
    Retries,
    RetryDelay,
    Only,
}

/// <summary>What a row shows (DESIGN D3): nobody sets it, everybody sets the same value, or the steps differ.</summary>
public enum BulkState
{
    Default,
    Set,
    Mixed,
}

/// <summary>
/// One shared row. <paramref name="Value"/> is the common value (a <see cref="FailurePolicy"/>, <see cref="TimeSpan"/>,
/// <see cref="int"/> or the "Only if" string) for Default and Set, null for Mixed. <paramref name="AnySet"/> drives "Reset all".
/// </summary>
public sealed record BulkRow(BulkField Field, BulkState State, object? Value, bool AnySet);

/// <summary>The outcome of <see cref="BulkEdit.Apply"/>: applied, or the problems the change would cause and nothing changed.</summary>
public sealed record BulkResult(bool Applied, IReadOnlyList<Problem> Problems);

/// <summary>
/// The settings shared by a selection of steps and cards, and the Set / Reset that writes them to every one through one
/// <see cref="FlowCanvasViewModel.Edit"/> (one Undo). Model only: the Many-mode inspector (T15.2) and params (T15.3) sit on it.
/// </summary>
public sealed class BulkEdit
{
    private readonly IReadOnlyList<Node> _nodes;
    private readonly Func<string, StepDefault?> _typeDefault;
    private readonly IReadOnlyList<StepNode> _steps;

    public BulkEdit(IEnumerable<Node> selected, Func<string, StepDefault?> typeDefault)
    {
        _nodes = [.. selected];
        _typeDefault = typeDefault;
        _steps = [.. _nodes.OfType<StepNode>()];
        var types = _steps.Select(s => s.Step).Distinct(StringComparer.Ordinal).ToList();
        StepType = types.Count == 1 && _nodes.Count == _steps.Count ? types[0] : null;
        Descriptor = StepType is null ? null : ProviderCatalog.Find(StepType);
        Rows = BuildRows();
    }

    public IReadOnlyList<Node> Nodes => _nodes;

    public int StepCount => _steps.Count;

    public int CardCount => _nodes.Count - _steps.Count;

    /// <summary>The step type when every selected step has the same one (and no card is selected).</summary>
    public string? StepType { get; }

    public ProviderDescriptor? Descriptor { get; }

    /// <summary>Steps only: cards have no timing or conditions.</summary>
    public bool HasTiming => _steps.Count > 0 && CardCount == 0;

    /// <summary>One type with conditions: the "Only if" choices.</summary>
    public IReadOnlyList<ConditionSpec> OnlyChoices => HasTiming && Descriptor is not null ? Descriptor.Conditions : [];

    public IReadOnlyList<BulkRow> Rows { get; }

    public BulkRow? Row(BulkField field) => Rows.FirstOrDefault(r => r.Field == field);

    private List<BulkRow> BuildRows()
    {
        var rows = new List<BulkRow>();
        if (_nodes.Count < 2)
            return rows;
        rows.Add(Build(BulkField.OnFailure, _nodes, n => n.OnFailure, n => n.OnFailure ?? (n is StepNode s ? Eff(s).OnFailure : FailurePolicy.Stop)));
        if (!HasTiming)
            return rows;
        rows.Add(Build(BulkField.Timeout, _steps, s => s.Timeout, s => Eff(s).Timeout));
        rows.Add(Build(BulkField.Retries, _steps, s => s.RetryCount, s => Eff(s).RetryCount));
        rows.Add(Build(BulkField.RetryDelay, _steps, s => s.RetryDelay, s => Eff(s).RetryDelay));
        if (OnlyChoices.Count > 0)
            rows.Add(Build(BulkField.Only, _steps, s => s.Only, s => Eff(s).Only ?? StepNode.OnlyAlways));
        return rows;
    }

    private StepSettings Eff(StepNode s) => StepSettings.Resolve(s, ProviderCatalog.Find(s.Step), _typeDefault(s.Step));

    private static BulkRow Build<TNode, T>(BulkField field, IEnumerable<TNode> items, Func<TNode, T?> own, Func<TNode, T> effective)
    {
        var list = items.ToList();
        var owns = list.Select(own).ToList();
        var anySet = owns.Any(o => o is not null);
        var allSet = owns.All(o => o is not null);
        if (anySet && !allSet)
            return new BulkRow(field, BulkState.Mixed, null, true); // partly inherited, even when the values agree
        var values = (allSet ? owns.Select(o => (object?)o) : list.Select(n => (object?)effective(n))).ToList();
        var same = values.All(v => Equals(v, values[0]));
        if (!same)
            return new BulkRow(field, BulkState.Mixed, null, anySet);
        return new BulkRow(field, anySet ? BulkState.Set : BulkState.Default, values[0], anySet);
    }

    /// <summary>Set <paramref name="field"/> on every selected step (or card, for the policy).</summary>
    public Func<Flow, Flow> Set(BulkField field, object value) => Change(field, value);

    /// <summary>Remove <paramref name="field"/> from every selected step: they follow the default again.</summary>
    public Func<Flow, Flow> Reset(BulkField field) => Change(field, null);

    private Func<Flow, Flow> Change(BulkField field, object? value)
    {
        if (Rows.All(r => r.Field != field))
            return f => f; // not shared by this selection: nothing to write
        var ids = _nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        return flow => flow with { Nodes = [.. flow.Nodes.Select(n => ids.Contains(n.Id) ? Write(n, field, value) : n)] };
    }

    private static Node Write(Node node, BulkField field, object? value)
    {
        if (field == BulkField.OnFailure)
            return node with { OnFailure = (FailurePolicy?)value };
        if (node is not StepNode s)
            return node;
        return field switch
        {
            BulkField.Timeout => s with { Timeout = (TimeSpan?)value },
            BulkField.Retries => s with { RetryCount = (int?)value },
            BulkField.RetryDelay => s with { RetryDelay = (TimeSpan?)value },
            BulkField.Only => s with { Only = (string?)value },
            _ => s,
        };
    }

    /// <summary>
    /// Writes <paramref name="change"/> (from <see cref="Set"/> / <see cref="Reset"/>) to the canvas as one Undo, after
    /// checking it: errors the change would add (per step, by path) are returned and nothing is applied, so it lands on
    /// all selected steps or none.
    /// </summary>
    public static BulkResult Apply(ConfigSession session, FlowCanvasViewModel canvas, Func<Flow, Flow> change)
    {
        if (!session.CanEdit || !canvas.CanEdit || canvas.Flow is not { } flow)
            return new BulkResult(false, []);
        var next = change(flow);
        if (next == flow)
            return new BulkResult(false, []);
        var config = session.Current with { Flows = [.. session.Current.Flows.Select(f => f.Id == flow.Id ? next : f)] };
        var before = session.Problems.ToHashSet();
        var added = ConfigValidator.Validate(config).Where(p => p.Severity == Severity.Error && !before.Contains(p)).ToList();
        if (added.Count > 0)
            return new BulkResult(false, added);
        canvas.Edit(_ => next);
        return new BulkResult(canvas.Flow == next, []);
    }
}
