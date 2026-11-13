using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;
using CtrlAltDaze.Editor.ViewModels.Inspector;
using CtrlAltDaze.Editor.ViewModels.Runs;
using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>Turning a step off in the editor (T25.4, D-148): Ctrl+/, the menu, the inspector, Undo, the pill and Runs.</summary>
public sealed class StepOffEditingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly FakeEngine _engine = new() { IsRunning = true, Answer = _ => new PipeMessage { Ok = true } };
    private readonly InspectorViewModel _inspector;

    public StepOffEditingTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
        _inspector = new InspectorViewModel(_session, _canvas, _engine, action => action(), _ => false);
    }

    public void Dispose()
    {
        _inspector.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private StepNode Step(string id) => (StepNode)_canvas.Flow!.FindNode(id)!;

    [Fact]
    public void TurnOffIsOneUndoStepAndThePillDims()
    {
        _canvas.Select("vol");
        Assert.False(_canvas.OffState);
        var undoBefore = _canvas.CanUndo;

        _canvas.ToggleOff();

        Assert.False(Step("vol").Enabled);
        Assert.True(_canvas.OffState);
        var pill = _canvas.Find("vol")!;
        Assert.True(pill.IsOff);
        Assert.Equal("Turned off", pill.Badges[0].Text);
        Assert.Contains("Turned off", pill.AutomationName, StringComparison.Ordinal);
        Assert.True(_canvas.CanUndo);
        Assert.Equal(["vol"], _canvas.SelectedIds); // the selection stays

        _canvas.Undo();
        Assert.True(Step("vol").Enabled);
        Assert.False(_canvas.Find("vol")!.IsOff);
        Assert.Equal(undoBefore, _canvas.CanUndo);
    }

    [Fact]
    public void ACardTurnsAllItsMembersOffAndAMixedSelectionTurnsOff()
    {
        _canvas.Select("w1");
        _canvas.ToggleOff();
        _canvas.Select("both");
        Assert.False(_canvas.OffState); // one member on: the menu says "Turn off"

        _canvas.ToggleOff();
        Assert.False(Step("w1").Enabled);
        Assert.False(Step("w2").Enabled);
        Assert.True(_canvas.OffState);

        _canvas.ToggleOff();
        Assert.True(Step("w1").Enabled && Step("w2").Enabled);
    }

    [Fact]
    public void NothingSelectedHasNoCommand()
    {
        _canvas.ClearSelection();
        Assert.Null(_canvas.OffState);
        _canvas.ToggleOff();
        Assert.False(_canvas.CanUndo);
    }

    [Fact]
    public void ACopyOfAnOffStepIsOffToo()
    {
        _canvas.Select("vol");
        _canvas.ToggleOff();
        _canvas.Duplicate();

        var copy = Assert.Single(_canvas.SelectedIds);
        Assert.False(Step(copy).Enabled);
    }

    [Fact]
    public void TheInspectorSwitchFollowsAndEditsTheStep()
    {
        _canvas.Select("vol");
        Assert.True(_inspector.StepOn);

        _inspector.StepOn = false;
        Assert.False(Step("vol").Enabled);

        _canvas.ToggleOff(); // back on from the canvas: the switch follows
        Assert.True(_inspector.StepOn);
    }

    [Fact]
    public void SavedYamlGetsEnabledFalseOnlyOnTheOffStep()
    {
        _canvas.Select("vol");
        _canvas.ToggleOff();

        var saved = ConfigYaml.Save(_session.Current);
        Assert.Single(saved.Split('\n'), l => l.Contains("enabled: false", StringComparison.Ordinal));
        Assert.False(((StepNode)ConfigYaml.Load(saved).Config!.Flows[0].FindNode("vol")!).Enabled);
    }

    [Fact]
    public void RunsSayTurnedOff()
    {
        var view = RunWords.View(new NodeResult("vol", NodeOutcome.Succeeded, Attempts: 0) { TurnedOff = true });

        Assert.Equal((NodeRunState.Skipped, "Turned off"), (view.State, view.Text));
    }
}
