using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T12.8: the <c>menu.show</c> inspector, the "Controller buttons" recorder, the template and trigger texts (DESIGN §3.7).</summary>
public sealed class MenuEditorTests : IDisposable
{
    private const string Yaml = """
        version: 1
        flows:
          couch:
            name: Couch
            gamepad: [View+Menu]
            nodes:
              - id: pick
                step: menu.show
                with: { items: [tv, desk, gone] }
          tv:
            name: Couch TV
            nodes:
              - { id: n, step: notify, with: { text: x } }
          desk:
            name: Desk
            enabled: false
            nodes:
              - { id: n, step: notify, with: { text: x } }
          night:
            name: Night
            gamepad: [LB+RB+Y]
            nodes:
              - { id: n, step: notify, with: { text: x } }
          plain:
            name: Plain
            nodes:
              - { id: n, step: notify, with: { text: x } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly FakeRecorder _recorder = new();
    private FlowCanvasViewModel _canvas = null!;
    private InspectorViewModel _inspector = null!;

    public MenuEditorTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        _engine.Answer = _ => new PipeMessage { Ok = true };
        Open("couch");
    }

    private void Open(string flowId)
    {
        _inspector?.Dispose();
        _canvas?.Dispose();
        _canvas = new FlowCanvasViewModel(_session, flowId);
        _inspector = new InspectorViewModel(_session, _canvas, _engine, action => action(), _ => true, recorder: _recorder);
    }

    public void Dispose()
    {
        _inspector.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class FakeRecorder : IGamepadRecorder
    {
        public GamepadRecording Next { get; set; } = new(GamepadRecordStatus.EngineOff);

        public int Calls { get; private set; }

        public bool Cancelled { get; private set; }

        public TaskCompletionSource<GamepadRecording>? Hold { get; set; }

        public async Task<GamepadRecording> RecordAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Hold is { } hold)
            {
                cancellationToken.Register(() => Cancelled = true);
                return await hold.Task;
            }
            return Next;
        }
    }

    private ParamFieldViewModel Items()
    {
        _canvas.Select("pick");
        return _inspector.Fields.Single(f => f.Name == "items");
    }

    private string[] Written() =>
        [.. ((ListParam)((StepNode)_canvas.Flow!.FindNode("pick")!).With["items"]).Items.Select(i => ((ScalarParam)i).Text)];

    // ---- Choices ----

    [Fact]
    public void ChoicesAreATokenListOfFlowsInMenuOrder()
    {
        var items = Items();
        Assert.True(items.IsFlowList);
        Assert.False(items.IsTokenList);
        Assert.False(items.IsFlowRef);
        Assert.Equal(["Couch TV", "Desk", "gone"], items.FlowTokens.Select(t => t.Name));
        Assert.Equal("Turned off", items.FlowTokens[1].Caption);
        Assert.Equal("Couch TV, 1 of 3", items.FlowTokens[0].AutomationName);
    }

    [Fact]
    public void AddingAFlowAppendsItsIdAndLeavesTheBox()
    {
        var items = Items();
        Assert.Contains(items.FlowChoices, c => c.Id == "night");
        items.AddFlow("night");
        Assert.Equal(["tv", "desk", "gone", "night"], Written());
        Assert.DoesNotContain(items.FlowChoices, c => c.Id == "night");
        items.AddFlow("night");
        Assert.Equal(4, Written().Length);
    }

    [Fact]
    public void OwnFlowIsNotOfferedAndAHandWrittenOneIsFlagged()
    {
        var items = Items();
        Assert.DoesNotContain(items.FlowChoices, c => c.Id == "couch");
        items.AddFlow("couch");
        var own = items.FlowTokens.Last();
        Assert.Equal(FlowTokens.OwnFlow, own.Problem);
        Assert.Contains("A menu can't list its own flow.", own.AutomationName);
    }

    [Fact]
    public void RemovingAndMovingRewriteTheList()
    {
        var items = Items();
        items.MoveFlow(items.FlowTokens[0], +1);
        Assert.Equal(["desk", "tv", "gone"], Written());
        items.MoveFlow(items.FlowTokens[0], -1);
        Assert.Equal(["desk", "tv", "gone"], Written());
        items.RemoveFlow(items.FlowTokens[0]);
        Assert.Equal(["tv", "gone"], Written());
        Assert.Equal(2, items.FlowTokens.Count);
    }

    [Fact]
    public void ADeletedFlowStaysAsACautionChipUntilRemoved()
    {
        var items = Items();
        var gone = items.FlowTokens[2];
        Assert.True(gone.IsMissing);
        Assert.Equal("gone isn't there anymore. Remove it from the menu.", gone.Problem);
        items.RemoveFlow(gone);
        Assert.DoesNotContain("gone", Written());
    }

    [Fact]
    public void TheMenuHoldsAtMostTwelveFlows()
    {
        var items = Items();
        for (var i = 0; i < 12; i++)
            items.AddFlow("f" + i);
        Assert.Equal(12, Written().Length);
        Assert.False(items.CanAddFlow);
    }

    [Fact]
    public void OtherSettingsKeepTheirDefaultsOutOfTheYaml()
    {
        _canvas.Select("pick");
        _inspector.Fields.Single(f => f.Name == "size").Choice = "large";
        _inspector.Fields.Single(f => f.Name == "layout").Choice = "list";
        _inspector.Fields.Single(f => f.Name == "closeAfter").Number = 45;
        _inspector.Fields.Single(f => f.Name == "wait").IsOn = true;
        var with = ((StepNode)_canvas.Flow!.FindNode("pick")!).With;
        Assert.Equal("large", ((ScalarParam)with["size"]).Text);
        Assert.Equal("45s", ((ScalarParam)with["closeAfter"]).Text);
        Assert.Equal("true", ((ScalarParam)with["wait"]).Text);
        Assert.False(with.ContainsKey("layout"));
    }

    // ---- Test this step ----

    [Fact]
    public void TestShowsTheMenuAndSaysWhatWasPicked()
    {
        _canvas.Select("pick");
        Assert.Equal("Show the menu", _inspector.TestLabel);
        Assert.Contains("Picking a choice doesn't run it", _inspector.TestTip);
        var worked = new NodeResult("pick", NodeOutcome.Succeeded);
        Assert.Equal("You picked Couch TV. Testing doesn't run it.", InspectorViewModel.ExplainMenu(worked, "Picked Couch TV"));
        Assert.Equal("Nothing picked.", InspectorViewModel.ExplainMenu(new NodeResult("pick", NodeOutcome.Cancelled)));
        Assert.StartsWith("Didn't work", InspectorViewModel.ExplainMenu(new NodeResult("pick", NodeOutcome.Failed, Error: "x")));
        Open("tv");
        _canvas.Select("n");
        Assert.Equal("Test this step", _inspector.TestLabel);
    }

    // ---- Controller buttons ----

    [Fact]
    public void ControllerButtonsShowTheFlowsChords()
    {
        var chip = Assert.Single(_inspector.ControllerButtons);
        Assert.Equal("View + Menu", chip.Text);
        Assert.False(chip.HasProblem);
        _inspector.RemoveChordCommand.Execute(chip);
        Assert.Empty(_canvas.Flow!.Gamepad);
        Assert.Empty(_inspector.ControllerButtons);
    }

    [Fact]
    public async Task ARecordedChordIsAddedInCanonicalOrder()
    {
        Open("plain");
        _recorder.Next = new(GamepadRecordStatus.Chord, "X+RB+LB");
        await _inspector.Recorder.StartAsync();
        Assert.Equal(RecorderPhase.Recorded, _inspector.Recorder.Phase);
        Assert.Equal(["X+LB+RB"], _canvas.Flow!.Gamepad);
        Assert.Equal("X + LB + RB", Assert.Single(_inspector.ControllerButtons).Text);
    }

    [Fact]
    public async Task ARecorderThatHearsNothingExplainsWhy()
    {
        Open("plain");
        _recorder.Next = new(GamepadRecordStatus.NoController);
        await _inspector.Recorder.StartAsync();
        Assert.Equal("Daze Control doesn't see a controller. Turn one on and try again.", _inspector.Recorder.Message);
        _recorder.Next = new(GamepadRecordStatus.EngineOff);
        await _inspector.Recorder.StartAsync();
        Assert.Equal("Start Daze Control to record controller buttons.", _inspector.Recorder.Message);
        _recorder.Next = new(GamepadRecordStatus.TimedOut);
        await _inspector.Recorder.StartAsync();
        Assert.True(_inspector.Recorder.IsProblem);
        Assert.Empty(_canvas.Flow!.Gamepad);
    }

    [Fact]
    public async Task OneButtonAndAnotherFlowsChordAreRefused()
    {
        Open("plain");
        _recorder.Next = new(GamepadRecordStatus.Chord, "A");
        await _inspector.Recorder.StartAsync();
        Assert.Equal("Use at least two buttons, so a game doesn't start it by accident.", _inspector.Recorder.Message);
        _recorder.Next = new(GamepadRecordStatus.Chord, "Menu+View");
        await _inspector.Recorder.StartAsync();
        Assert.Equal("Another flow uses View + Menu. Try another one.", _inspector.Recorder.Message);
        Assert.Empty(_canvas.Flow!.Gamepad);
    }

    [Fact]
    public async Task ARecordedChordTheFlowAlreadyHasIsNotAddedTwice()
    {
        _recorder.Next = new(GamepadRecordStatus.Chord, "Menu+View");
        await _inspector.Recorder.StartAsync();
        Assert.Equal(["View+Menu"], _canvas.Flow!.Gamepad);
    }

    [Fact]
    public async Task ClosingTheFlyoutStopsTheRecording()
    {
        Open("plain");
        _recorder.Hold = new();
        var recording = _inspector.Recorder.StartAsync();
        Assert.True(_inspector.Recorder.IsListening);
        Assert.Equal("Press the buttons together on your controller, then let go.", _inspector.Recorder.Message);
        await _inspector.Recorder.StartAsync();
        Assert.Equal(1, _recorder.Calls);
        _inspector.Recorder.Cancel();
        _recorder.Hold.SetResult(new(GamepadRecordStatus.Chord, "Y+LB"));
        await recording;
        Assert.True(_recorder.Cancelled);
        Assert.Equal(RecorderPhase.Idle, _inspector.Recorder.Phase);
        Assert.Empty(_canvas.Flow!.Gamepad);
    }

    [Fact]
    public void TheEngineRecorderReadsDesignsAnswers()
    {
        Assert.Equal(GamepadRecordStatus.EngineOff, EngineGamepadRecorder.Interpret(null).Status);
        Assert.Equal("Y+LB", EngineGamepadRecorder.Interpret(new PipeMessage { Ok = true, Yaml = " Y+LB " }).Chord);
        Assert.Equal(GamepadRecordStatus.NoController, EngineGamepadRecorder.Interpret(new PipeMessage { Error = "No controller found." }).Status);
        Assert.Equal(GamepadRecordStatus.TimedOut, EngineGamepadRecorder.Interpret(new PipeMessage { Error = "Timed out." }).Status);
    }

    // ---- Template and trigger texts ----

    [Fact]
    public void TheControllerMenuTemplateHoldsAMenuOfTheFirstFourFlows()
    {
        var flow = FlowsPageViewModel.ControllerMenu(_session.Current, "controller-menu");
        Assert.Equal("Controller menu", flow.Name);
        Assert.Equal(["View+Menu"], flow.Gamepad);
        var step = Assert.IsType<StepNode>(Assert.Single(flow.Nodes));
        Assert.Equal("menu.show", step.Step);
        Assert.Equal(["couch", "tv", "desk", "night"], ((ListParam)step.With["items"]).Items.Select(i => ((ScalarParam)i).Text));
        Assert.Equal("large", ((ScalarParam)step.With["size"]).Text);
        var empty = FlowsPageViewModel.ControllerMenu(new Configuration(), "controller-menu");
        Assert.False(((StepNode)empty.Nodes[0]).With.ContainsKey("items"));
    }

    [Fact]
    public void TriggerTextsNameTheChordAndTheMenu()
    {
        Assert.Equal("Controller · View + Menu", TriggerTexts.Controller("Menu+View"));
        Assert.Equal("Menu · Couch", TriggerTexts.Menu("Couch"));
    }
}
