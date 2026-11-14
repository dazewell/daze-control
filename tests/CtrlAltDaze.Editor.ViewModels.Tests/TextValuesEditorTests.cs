using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;
using CtrlAltDaze.Editor.ViewModels.Inspector;
using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>T23.7 in the editor: text inputs, a call's text inputs, "Values it gives back", and a call's values in Insert value.</summary>
public sealed class TextValuesEditorTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC", name: Technics }
        flows:
          inner:
            name: Connect headphones
            inputs:
              greeting: { kind: text, name: Greeting, default: Hi }
            outputs:
              device: "{bt.device}"
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }
          caller:
            name: Evening
            nodes:
              - { id: first, step: bluetooth.connect, with: { device: technics } }
              - { id: call, step: flow.run, with: { flow: inner }, after: [first] }
              - { id: note, step: notify, with: { text: "{call.device} ready" }, after: [call] }
              - { id: line, step: text.make, with: { text: "x" }, after: [call] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private FlowCanvasViewModel _canvas = null!;
    private InspectorViewModel _inspector = null!;

    public TextValuesEditorTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        Assert.DoesNotContain(_session.Problems, p => p.Severity == Severity.Error);
        _engine.Answer = _ => new PipeMessage { Ok = true };
        Open("caller");
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

    private ParamFieldViewModel Field(string name) => _inspector.Fields.Single(f => f.Name == name);

    [Fact]
    public void ATextInputShowsItsDefaultTextAndNoDevicePicker()
    {
        Open("inner");

        var input = Assert.Single(_inspector.Inputs);
        Assert.True(input.IsText);
        Assert.False(input.IsDevice);
        Assert.Equal("Text", FlowInputViewModel.KindNames[input.KindIndex]);
        Assert.Equal("Hi", input.TextDefault);

        input.TextDefault = "Hello";

        Assert.Equal(new FlowInput(null, "Greeting", "Hello"), _canvas.Flow!.Inputs["greeting"]);
    }

    [Fact]
    public void MakingAnInputTextKeepsStepsAsTheyWereAndBackToADevice()
    {
        Open("inner");
        _inspector.AddInputCommand.Execute(null);
        var audio = _inspector.Inputs.Single(i => i.Name == "audio");

        audio.KindIndex = FlowInputViewModel.KindNames.Count - 1;

        Assert.Equal(new FlowInput(null, "Text", ""), _canvas.Flow!.Inputs["audio"]);
        audio = _inspector.Inputs.Single(i => i.Name == "audio");
        audio.KindIndex = 0;
        Assert.Equal(LibraryKind.Audio, _canvas.Flow!.Inputs["audio"].Kind);
    }

    [Fact]
    public void ACallsTextInputIsATextBoxThatPassesWhatIsTyped()
    {
        _canvas.Select("call");
        var row = Assert.Single(Field("inputs").CallInputs);
        Assert.True(row.IsText);
        Assert.Equal("Its default: Hi", row.TextPlaceholder);

        row.Text = "Hello {first.device}";

        var call = (StepNode)_canvas.Flow!.FindNode("call")!;
        Assert.Equal("Hello {first.device}", FlowInputs.Given(call)["greeting"]);
        row = Assert.Single(Field("inputs").CallInputs);
        row.Text = "";
        Assert.Empty(FlowInputs.Given((StepNode)_canvas.Flow!.FindNode("call")!));
    }

    [Fact]
    public void ACallsValuesAreInTheInsertValueListAndPills()
    {
        _canvas.Select("note");
        var text = Field("text");

        var group = Assert.Single(text.ValueGroups, g => g.Header == "Run flow");
        Assert.Equal(["Device"], group.Rows.Select(r => r.Title));
        Assert.Equal("{Run flow: Device} ready", text.Text);
        Assert.False(text.HasUnknownValue);
    }

    [Fact]
    public void MakeTextTakesValuesAndGivesText()
    {
        _canvas.Select("line");
        var text = Field("text");

        Assert.True(text.TakesValues);
        Assert.Contains(text.ValueGroups, g => g.Header == "Run flow");
        var given = InspectorViewModel.GivenValuesOf(_canvas.Flow!, (StepNode)_canvas.Flow!.FindNode("line")!);
        Assert.Equal(["Text"], given.Select(v => v.Title));
    }

    [Fact]
    public void ValuesItGivesBackAreListedEditedAndRemovedWithOneUndoEach()
    {
        Open("inner");
        var output = Assert.Single(_inspector.Outputs);
        Assert.Equal("device", output.Name);
        Assert.Equal("{Connect: Device}", output.Text);

        output.Text = "{Connect: Device} ok";
        Assert.Equal("{bt.device} ok", _canvas.Flow!.Outputs["device"]);

        _inspector.AddOutputCommand.Execute(null);
        Assert.Equal(["device", "value"], _canvas.Flow!.Outputs.Keys);
        _canvas.Undo();
        Assert.Equal(["device"], _canvas.Flow!.Outputs.Keys);

        var insert = Assert.Single(_inspector.Outputs).ValueGroups.SelectMany(g => g.Rows).Single(r => r.Title == "Device");
        Assert.Equal("{bt.device}", insert.Stored);
    }

    [Fact]
    public void RenamingAValueAnotherFlowUsesSaysSoAndItsCallerWarns()
    {
        Open("inner");
        var output = Assert.Single(_inspector.Outputs);

        output.EditName = "headphones";

        Assert.Equal(["headphones"], _canvas.Flow!.Outputs.Keys);
        Assert.Contains("Evening uses \"device\"", _inspector.OutputTip, StringComparison.Ordinal);
        Assert.Contains(_session.Problems, p => p is { Severity: Severity.Warning, FlowId: "caller" }
            && p.Message.StartsWith("Connect headphones doesn't give a value called \"device\"", StringComparison.Ordinal));
        _canvas.Undo();
        Assert.Equal(["device"], _canvas.Flow!.Outputs.Keys);
    }

    [Fact]
    public void ABadOrTakenNameIsRefusedAndSaid()
    {
        Open("inner");
        _inspector.AddOutputCommand.Execute(null);

        _inspector.Outputs.Single(o => o.Name == "value").EditName = "device";

        Assert.Equal(["device", "value"], _canvas.Flow!.Outputs.Keys);
        Assert.Equal("There's already a value called \"device\".", _inspector.Outputs.Single(o => o.Name == "value").Error);
    }
}
