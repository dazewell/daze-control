using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T13.10: flow inputs in flow settings, <c>$name</c> in device pickers, and "Run a flow"'s input pickers (UX §5.4).</summary>
public sealed class FlowInputsEditorTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC", name: Technics }
            sony: { address: "AC:80:0A:11:22:33", name: Sony }
        flows:
          make-default:
            name: Make it the default
            tray: { show: false }
            inputs:
              output:    { kind: audio,     name: Output,           default: technics }
              bluetooth: { kind: bluetooth, name: Bluetooth device, default: technics }
            nodes:
              - { id: set,     step: audio.setDefault,  with: { device: $output }, onFailure: skip }
              - { id: connect, step: bluetooth.connect, with: { device: $bluetooth }, after: [{ id: set, when: failure }] }
          headphones:
            name: Headphones
            inputs:
              mine: { kind: audio, name: My output, default: sony }
            nodes:
              - id: call
                step: flow.run
                with: { flow: make-default, inputs: { output: sony } }
          night:
            name: Night
            nodes:
              - { id: n, step: notify, with: { text: x } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private FlowCanvasViewModel _canvas = null!;
    private InspectorViewModel _inspector = null!;

    public FlowInputsEditorTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        Assert.DoesNotContain(_session.Problems, p => p.Severity == Severity.Error);
        _engine.Answer = _ => new PipeMessage { Ok = true };
        Open("headphones");
    }

    private void Open(string flowId)
    {
        _inspector?.Dispose();
        _canvas?.Dispose();
        _canvas = new FlowCanvasViewModel(_session, flowId);
        _inspector = new InspectorViewModel(_session, _canvas, _engine, action => action(), _ => true);
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

    private ParamFieldViewModel Field(string name) => _inspector.Fields.Single(f => f.Name == name);

    private CallInputViewModel CallInput(string name) => Field("inputs").CallInputs.Single(c => c.Name == name);

    // ---- Flow settings: Inputs ----

    [Fact]
    public void FlowSettingsListTheInputsWithKindAndDefault()
    {
        Open("make-default");

        Assert.True(_inspector.HasInputs);
        Assert.Equal(["output", "bluetooth"], _inspector.Inputs.Select(i => i.Name));
        var output = _inspector.Inputs[0];
        Assert.Equal("Output", output.Label);
        Assert.Equal(LibraryKind.Audio, output.Kind);
        Assert.Equal("technics", output.Device?.Key);
        Assert.Equal(LibraryKind.Bluetooth, _inspector.Inputs[1].Kind);
    }

    [Fact]
    public void AddAnInputMakesAnAudioInputWithTheFirstDevice()
    {
        Open("night");
        Assert.False(_inspector.HasInputs);

        _inspector.AddInputCommand.Execute(null);

        var input = Assert.Single(_canvas.Flow!.Inputs);
        Assert.Equal("audio", input.Key);
        Assert.Equal(new FlowInput(LibraryKind.Audio, "Audio device", "technics"), input.Value);
        Assert.Equal("Audio device", Assert.Single(_inspector.Inputs).Label);
        _canvas.Undo();
        Assert.Empty(_canvas.Flow!.Inputs);
    }

    [Fact]
    public void RenamingAnInputKeepsItsKeySoStepsStillWork()
    {
        Open("make-default");

        _inspector.Inputs[0].Label = "Speakers";

        Assert.Equal("Speakers", _canvas.Flow!.Inputs["output"].Label);
        Assert.Equal(new ScalarParam("$output"), Step("set").With["device"]);
    }

    [Fact]
    public void PickingADefaultWritesIt()
    {
        Open("make-default");
        var output = _inspector.Inputs[0];

        output.Device = output.Devices.Single(d => d.Key == "sony");

        Assert.Equal("sony", _canvas.Flow!.Inputs["output"].Default);
    }

    [Fact]
    public void RemovingAUsedInputWritesItsDefaultIntoTheStepsInOneUndoStep()
    {
        Open("make-default");

        _inspector.Inputs[0].Remove();

        Assert.False(_canvas.Flow!.Inputs.ContainsKey("output"));
        Assert.Equal(new ScalarParam("technics"), Step("set").With["device"]);
        Assert.Equal("Steps that used Output now use Technics.", _inspector.InputTip);
        _canvas.Undo();
        Assert.True(_canvas.Flow!.Inputs.ContainsKey("output"));
        Assert.Equal(new ScalarParam("$output"), Step("set").With["device"]);
    }

    [Fact]
    public void ChangingAnInputsKindDetachesItsStepsAndPicksADeviceOfTheNewKind()
    {
        Open("make-default");

        _inspector.Inputs[0].KindIndex = 1; // Bluetooth device

        var input = _canvas.Flow!.Inputs["output"];
        Assert.Equal(LibraryKind.Bluetooth, input.Kind);
        Assert.Equal("technics", input.Default);
        Assert.Equal(new ScalarParam("technics"), Step("set").With["device"]);
        Assert.NotNull(_inspector.InputTip);
    }

    // ---- $name in device pickers ----

    [Fact]
    public void ADevicePickerOffersTheFlowsInputsOfItsKind()
    {
        Open("make-default");
        _canvas.Select("set");

        var field = Field("device");
        Assert.Equal(DeviceSource.Input, field.Device?.Source);
        Assert.Equal("Output", field.Device?.Title);
        Assert.Equal("Technics unless the caller picks another", field.Device?.Caption);
        Assert.Equal(LibraryDevices.InputsGroup, field.Devices[0].Title);
        Assert.True(field.Devices[0].IsHeader);
        Assert.False(field.Devices[0].IsEnabled);
        Assert.DoesNotContain(field.Devices, d => d.Key == "$bluetooth"); // another kind

        field.Device = field.Devices.Single(d => d.Key == "sony");
        Assert.Equal(new ScalarParam("sony"), Step("set").With["device"]);
        Field("device").Device = Field("device").Devices.Single(d => d.Key == "$output");
        Assert.Equal(new ScalarParam("$output"), Step("set").With["device"]);
    }

    [Fact]
    public void AHeadingCantBePicked()
    {
        Open("make-default");
        _canvas.Select("set");

        Field("device").Device = Field("device").Devices[0];

        Assert.Equal(new ScalarParam("$output"), Step("set").With["device"]);
        Assert.Equal("$output", Field("device").Device?.Key);
    }

    // ---- Run a flow ----

    [Fact]
    public void ACallShowsOnePickerPerInputOfTheCalledFlow()
    {
        _canvas.Select("call");

        var field = Field("inputs");
        Assert.True(field.IsCallInputs);
        Assert.False(field.IsMultiline);
        Assert.False(field.HasExamples);
        Assert.Equal(["output", "bluetooth"], field.CallInputs.Select(c => c.Name));
        Assert.Equal("sony", CallInput("output").Device?.Key);
        Assert.Equal(DeviceSource.None, CallInput("bluetooth").Device?.Source);
        Assert.Equal("Its default: Technics", CallInput("bluetooth").Device?.Title);
        Assert.Equal("make-default", _inspector.CalledFlowId);
    }

    [Fact]
    public void ACallsPickerOffersTheCallersOwnInputsOfThatKind()
    {
        _canvas.Select("call");

        var output = CallInput("output");
        Assert.Contains(output.Devices, d => d.IsHeader && d.Title == InspectorViewModel.FromThisFlowsInputs);
        var mine = output.Devices.Single(d => d.Key == "$mine");
        Assert.Equal("My output", mine.Title);
        Assert.DoesNotContain(CallInput("bluetooth").Devices, d => d.Source == DeviceSource.Input);

        output.Device = mine;

        Assert.Equal("$mine", FlowInputs.Given(Step("call"))["output"]);
        Assert.Equal("$mine", CallInput("output").Device?.Key);
    }

    [Fact]
    public void PickingAndClearingACallsInputWritesTheMap()
    {
        _canvas.Select("call");

        CallInput("bluetooth").Device = CallInput("bluetooth").Devices.Single(d => d.Key == "sony");
        Assert.Equal(new Dictionary<string, string> { ["output"] = "sony", ["bluetooth"] = "sony" }, FlowInputs.Given(Step("call")));

        CallInput("output").Device = CallInput("output").Devices[0]; // its default
        CallInput("bluetooth").Device = CallInput("bluetooth").Devices[0];
        Assert.False(Step("call").With.ContainsKey(FlowInputs.Param));
    }

    [Fact]
    public void PickingAnotherFlowDropsTheInputsItDoesntTake()
    {
        _canvas.Select("call");

        Field("flow").Device = Field("flow").Devices.Single(d => d.Key == "night");

        Assert.False(Step("call").With.ContainsKey(FlowInputs.Param));
        Assert.Empty(Field("inputs").CallInputs);
        Assert.Equal("Night takes no inputs.", Field("inputs").CallInputsHint);
    }

    [Fact]
    public void WithNoFlowChosenTheInputsSayToChooseOneFirst()
    {
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, new StepNode("empty", Core.Catalog.ProviderCatalog.RunFlowId)] });
        _canvas.Select("empty");

        Assert.Empty(Field("inputs").CallInputs);
        Assert.Equal(InspectorViewModel.ChooseAFlowFirst, Field("inputs").CallInputsHint);
        Assert.Null(_inspector.CalledFlowId);
    }

    [Fact]
    public void AnInputTheCalledFlowDoesntHaveIsSaidUnderTheField()
    {
        _canvas.Edit(f => f with
        {
            Nodes = [.. f.Nodes.Select(n => n is StepNode { Id: "call" } s
                ? s with { With = new([KeyValuePair.Create("flow", (ParamValue)"make-default"),
                    KeyValuePair.Create(FlowInputs.Param, (ParamValue)new MapParam(new([KeyValuePair.Create("gone", (ParamValue)"sony")])))]) }
                : n)],
        });
        _canvas.Select("call");

        Assert.Equal("Make it the default has no input called \"gone\".", Field("inputs").Error);
    }

    [Fact]
    public void TheFlowPickerSaysWhatEachFlowTakesAndListsLoopsOff()
    {
        _canvas.Select("call");
        Assert.Equal("Takes an output and a Bluetooth device", Field("flow").Devices.Single(r => r.Key == "make-default").Caption);

        Open("make-default");
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, new StepNode("back", Core.Catalog.ProviderCatalog.RunFlowId)] });
        _canvas.Select("back");

        var rows = Field("flow").Devices;
        Assert.Equal(["Night", "Headphones", "Make it the default"], rows.Select(r => r.Title));
        Assert.True(rows[0].IsEnabled);
        Assert.False(rows[1].IsEnabled); // Headphones runs Make it the default
        Assert.Equal("That would make a loop: Make it the default runs Headphones, which runs Make it the default.", rows[1].Caption);
        Assert.False(rows[2].IsEnabled);
        Assert.Equal("A flow can't run itself.", rows[2].Caption);
    }

    [Fact]
    public void OpenThisFlowAsksTheShellToOpenTheCalledFlow()
    {
        string? opened = null;
        _inspector.OpenFlowRequested += id => opened = id;
        _canvas.Select("call");

        Assert.True(_inspector.CanOpenCalledFlow);
        _inspector.OpenCalledFlowCommand.Execute(null);

        Assert.Equal("make-default", opened);
    }
}
