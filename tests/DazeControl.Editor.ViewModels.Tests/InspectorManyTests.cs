using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T15.2: the Many-mode inspector: header, Mixed placeholders and captions, Set / Reset on all, one Undo (D-084).</summary>
public sealed class InspectorManyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly InspectorViewModel _inspector;

    public InspectorManyTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
        _inspector = new InspectorViewModel(_session, _canvas, new FakeEngine { IsRunning = true }, action => action(), _ => true);
    }

    public void Dispose()
    {
        _inspector.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static readonly string[] Three = ["bt", "out", "vol"];

    private StepNode Step(string id) => (StepNode)_canvas.Flow!.FindNode(id)!;

    [Fact]
    public void HintIsAnnouncedWhenTheSelectionBecomesMany()
    {
        var raised = new List<string?>();
        _inspector.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _canvas.SelectOnly(["w1", "w2"]);
        Assert.Contains(nameof(InspectorViewModel.ManyHint), raised); // F-032: the OneWay binding needs it
        Assert.Contains("Del deletes them all", _inspector.ManyHint);
    }

    [Fact]
    public void HeaderCountsAndNamesTheType()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        Assert.True(_inspector.IsMany);
        Assert.Equal("2 steps selected · Wait", _inspector.ManyHeader);

        _canvas.SelectOnly(["w1", "vol"]);
        Assert.Equal("2 steps selected", _inspector.ManyHeader);

        _canvas.SelectOnly(["w1", "both"]);
        Assert.Equal("1 step and 1 card selected", _inspector.ManyHeader);
    }

    [Fact]
    public void ManyHintNamesTheBulkActions()
    {
        _canvas.SelectOnly(["w1", "vol"]);
        Assert.Contains("Del", _inspector.ManyHint);
        Assert.Contains("Ctrl+Z", _inspector.ManyHint);
    }

    [Fact]
    public void BigDeleteAsksFirstAndEditsAreCounted()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        Assert.False(_canvas.DeleteNeedsConfirm);
        Assert.Equal(0, _canvas.EditCount);
        _canvas.DeleteSelection();
        Assert.Equal(1, _canvas.EditCount);
        Assert.Equal(10, FlowCanvasViewModel.BigDeleteThreshold);
    }

    [Fact]
    public void UniformSelectionShowsTheValueAsDefault()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        Assert.Equal(0, _inspector.ManyPolicyIndex);
        Assert.Equal("Default", _inspector.ManyPolicyView.Caption);
        Assert.Contains("default", _inspector.ManyTimeoutView.Name);
        Assert.False(_inspector.ManyTimeoutView.ShowReset);
        Assert.True(_inspector.ManyHasTiming);
    }

    [Fact]
    public void DifferingValuesAreMixed()
    {
        _canvas.SelectOnly(["bt", "out", "vol"]);
        Assert.Equal(-1, _inspector.ManyPolicyIndex); // out ignores, the others stop
        Assert.Equal("Mixed", _inspector.ManyPolicyView.Caption);
        Assert.True(_inspector.ManyPolicyView.ShowReset);
        Assert.Equal("If it fails, mixed", _inspector.ManyPolicyView.Name);
        Assert.True(double.IsNaN(_inspector.ManyTimeout));
        Assert.Equal("Give up after, mixed", _inspector.ManyTimeoutView.Name);
        Assert.False(_inspector.ManyHasOnlyIf);
    }

    [Fact]
    public void ACardLeavesOnlyThePolicy()
    {
        _canvas.SelectOnly(["w1", "both"]);
        Assert.False(_inspector.ManyHasTiming);
        Assert.False(_inspector.ManyHasOnlyIf);
        Assert.Equal(0, _inspector.ManyPolicyIndex);
    }

    [Fact]
    public void ATimeoutIsSetOnEveryStepAsOneUndo()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        _inspector.ManyTimeout = 9;

        Assert.Equal(TimeSpan.FromSeconds(9), Step("w1").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(9), Step("w2").Timeout);
        Assert.True(_inspector.ManyTimeoutView.ShowReset);
        Assert.Equal(9, _inspector.ManyTimeout);

        _canvas.Undo();
        Assert.Null(Step("w1").Timeout);
        Assert.Null(Step("w2").Timeout);
    }

    [Fact]
    public void PolicyWritesEveryStepAndResetClearsIt()
    {
        _canvas.SelectOnly(["bt", "out", "vol"]);
        _inspector.ManyPolicyIndex = 2;
        Assert.All(Three, id => Assert.Equal(FailurePolicy.Ignore, Step(id).OnFailure));

        _inspector.ResetManyPolicyCommand.Execute(null);
        Assert.All(Three, id => Assert.Null(Step(id).OnFailure));
        Assert.Equal(0, _inspector.ManyPolicyIndex);
        Assert.Equal("Default", _inspector.ManyPolicyView.Caption);
    }

    [Fact]
    public void ResetAllNeedsOnlyOneStepToSetTheField()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes.Select(n => n.Id == "w1" ? Step("w1") with { Timeout = TimeSpan.FromSeconds(4) } : n)] });
        Assert.Equal("Mixed", _inspector.ManyTimeoutView.Caption);
        Assert.True(_inspector.ManyTimeoutView.ShowReset);
        Assert.Equal("Use the default for all 2 steps", _inspector.ManyTimeoutView.ResetTip);

        _inspector.ResetManyTimeoutCommand.Execute(null);
        Assert.Null(Step("w1").Timeout);
        Assert.Equal("Default", _inspector.ManyTimeoutView.Caption);
    }

    [Fact]
    public void RetriesAreSetOnAllAndTheDelayFollows()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        _inspector.ManyRetries = 2;
        Assert.Equal(2, Step("w1").RetryCount);
        Assert.Equal(2, Step("w2").RetryCount);
        Assert.True(_inspector.ManyCanRetryDelay);
    }

    [Fact]
    public void FirstRetryWaitsOneSecondAndNoRetriesDropTheDelay()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        _inspector.ManyRetries = 2;
        Assert.Equal(TimeSpan.FromSeconds(1), Step("w1").RetryDelay);
        _inspector.ManyRetries = 0;
        Assert.Null(Step("w2").RetryDelay);
    }

    [Fact]
    public void ZeroTimeoutClearsItOnAll()
    {
        _canvas.SelectOnly(["w1", "w2"]);
        _inspector.ManyTimeout = 9;
        _inspector.ManyTimeout = 0;
        Assert.Null(Step("w1").Timeout);
        Assert.Null(Step("w2").Timeout);
    }

    [Fact]
    public void OnlyIfIsSetOnAllStepsOfOneType()
    {
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, Step("bt") with { Id = "bt2" }] });
        _canvas.SelectOnly(["bt", "bt2"]);
        Assert.True(_inspector.ManyHasOnlyIf);
        Assert.Equal(0, _inspector.ManyOnlyIndex);
        Assert.True(_inspector.ManyOnlyOptions.Count > 1);

        _inspector.ManyOnlyIndex = 1;
        Assert.NotNull(Step("bt").Only);
        Assert.Equal(Step("bt").Only, Step("bt2").Only);
        Assert.True(_inspector.ManyOnlyView.ShowReset);

        _inspector.ResetManyOnlyCommand.Execute(null);
        Assert.Null(Step("bt").Only);
        Assert.Null(Step("bt2").Only);
    }

    [Fact]
    public void SelectAllOfTypeSelectsTheSameKindOfStepOnly()
    {
        _canvas.Select("w1");
        Assert.Equal("wait", _canvas.SelectedStepType);
        _canvas.SelectAllOfType("wait");
        Assert.Equal(["w1", "w2"], _canvas.SelectedIds.Order());
        Assert.Null(_canvas.SelectedStepType);
    }
}
