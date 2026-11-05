using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T15.1: what several selected steps share, Mixed, Set / Reset on all, one Undo, all-or-none (D-084).</summary>
public sealed class BulkEditTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;

    public BulkEditTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static readonly string[] Three = ["w1", "w2", "vol"];

    private BulkEdit Bulk(params string[] ids) =>
        new(ids.Select(i => _canvas.Flow!.FindNode(i)!), id => _session.Current.Settings.StepDefaults.TryGetValue(id, out var d) ? d : null);

    private StepNode Step(string id) => (StepNode)_canvas.Flow!.FindNode(id)!;

    [Fact]
    public void OneTypeSharesPolicyTimingAndOnlyIf()
    {
        var bulk = Bulk("w1", "w2");
        Assert.Equal("wait", bulk.StepType);
        Assert.True(bulk.HasTiming);
        Assert.Equal([BulkField.OnFailure, BulkField.Timeout, BulkField.Retries, BulkField.RetryDelay], bulk.Rows.Select(r => r.Field));

        var conditional = Bulk("bt", "bt");
        Assert.Contains(conditional.Rows, r => r.Field == BulkField.Only);
    }

    [Fact]
    public void SeveralTypesShareTimingButNotOnlyIf()
    {
        var bulk = Bulk("bt", "out", "vol");
        Assert.Null(bulk.StepType);
        Assert.DoesNotContain(bulk.Rows, r => r.Field == BulkField.Only);
        Assert.Contains(bulk.Rows, r => r.Field == BulkField.Timeout);
    }

    [Fact]
    public void StepsAndCardsShareOnlyThePolicy()
    {
        var bulk = Bulk("w1", "both");
        Assert.Equal(1, bulk.CardCount);
        Assert.Equal([BulkField.OnFailure], bulk.Rows.Select(r => r.Field));
        Assert.Equal([BulkField.OnFailure], Bulk("both", "both").Rows.Select(r => r.Field));
    }

    [Fact]
    public void ACardInTheSelectionDropsTheType() => Assert.Null(Bulk("w1", "w2", "both").StepType);

    [Fact]
    public void ExplicitAndInheritedAreMixedEvenWhenEqual()
    {
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes.Select(n => n.Id == "w1" ? ((StepNode)n) with { OnFailure = FailurePolicy.Stop } : n)] });
        var row = Bulk("w1", "w2").Row(BulkField.OnFailure)!;
        Assert.Equal(BulkState.Mixed, row.State);
        Assert.True(row.AnySet);
    }

    [Fact]
    public void ASingleItemHasNoBulkRows() => Assert.Empty(Bulk("w1").Rows);

    [Fact]
    public void UnsetEverywhereIsDefaultAndSameValuesAreSet()
    {
        var wait = Bulk("w1", "w2").Row(BulkField.Timeout)!;
        Assert.Equal(BulkState.Default, wait.State);
        Assert.False(wait.AnySet);

        _canvas.Edit(f => f with { Nodes = [.. f.Nodes.Select(n => n.Id is "w1" or "w2" ? ((StepNode)n) with { Timeout = TimeSpan.FromSeconds(4) } : n)] });
        var set = Bulk("w1", "w2").Row(BulkField.Timeout)!;
        Assert.Equal(BulkState.Set, set.State);
        Assert.Equal(TimeSpan.FromSeconds(4), set.Value);
    }

    [Fact]
    public void DifferentOrPartlyInheritedValuesAreMixed()
    {
        // vol sets 5 s, bt inherits the provider default: partly inherited
        var partly = Bulk("vol", "bt").Row(BulkField.Timeout)!;
        Assert.Equal(BulkState.Mixed, partly.State);
        Assert.True(partly.AnySet);

        // out ignores, bt inherits stop
        var policy = Bulk("out", "bt").Row(BulkField.OnFailure)!;
        Assert.Equal(BulkState.Mixed, policy.State);

        // all inherit but the effective values differ through a type default
        var differing = new BulkEdit([Step("w1"), Step("w2")], id => new StepDefault { Timeout = TimeSpan.FromSeconds(id == "wait" ? 9 : 1) });
        Assert.Equal(BulkState.Default, differing.Row(BulkField.Timeout)!.State);
        var mixedByType = new BulkEdit([Step("w1"), Step("out")], id => new StepDefault { Timeout = TimeSpan.FromSeconds(id == "wait" ? 9 : 1) });
        Assert.Equal(BulkState.Mixed, mixedByType.Row(BulkField.Timeout)!.State);
        Assert.False(mixedByType.Row(BulkField.Timeout)!.AnySet);
    }

    [Fact]
    public void SetWritesEveryStepAndResetClearsEveryStep()
    {
        var bulk = Bulk("w1", "w2", "vol");
        var result = BulkEdit.Apply(_session, _canvas, bulk.Set(BulkField.Timeout, TimeSpan.FromSeconds(7)));
        Assert.True(result.Applied);
        Assert.All(Three, id => Assert.Equal(TimeSpan.FromSeconds(7), Step(id).Timeout));

        Assert.True(BulkEdit.Apply(_session, _canvas, Bulk("w1", "w2", "vol").Reset(BulkField.Timeout)).Applied);
        Assert.All(Three, id => Assert.Null(Step(id).Timeout));
    }

    [Fact]
    public void PolicyAndOnlyIfAreWrittenAndCardsTakeThePolicy()
    {
        Assert.True(BulkEdit.Apply(_session, _canvas, Bulk("w1", "both").Set(BulkField.OnFailure, FailurePolicy.Ignore)).Applied);
        Assert.Equal(FailurePolicy.Ignore, Step("w1").OnFailure);
        Assert.Equal(FailurePolicy.Ignore, _canvas.Flow!.FindNode("both")!.OnFailure);

        Assert.True(BulkEdit.Apply(_session, _canvas, Bulk("bt", "bt").Set(BulkField.Only, "disconnected")).Applied);
        Assert.Equal("disconnected", Step("bt").Only);
    }

    [Fact]
    public void OneUndoRestoresEverything()
    {
        var before = _canvas.Flow;
        Assert.True(BulkEdit.Apply(_session, _canvas, Bulk("w1", "w2", "vol").Set(BulkField.Retries, 2)).Applied);
        _canvas.Undo();
        Assert.Equal(before, _canvas.Flow);
        Assert.False(_canvas.CanUndo);
    }

    [Fact]
    public void ANewErrorStopsTheWholeChange()
    {
        var before = _canvas.Flow;
        var result = BulkEdit.Apply(_session, _canvas, Bulk("w1", "w2").Set(BulkField.Timeout, TimeSpan.FromDays(40)));
        Assert.False(result.Applied);
        Assert.NotEmpty(result.Problems);
        Assert.Equal(before, _canvas.Flow);
        Assert.False(_canvas.CanUndo);
    }
}
