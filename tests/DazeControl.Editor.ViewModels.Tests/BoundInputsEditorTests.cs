using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Bound devices as flow inputs (D-125): the Inputs section's kind and default, and the step pickers that take them.</summary>
public sealed class BoundInputsEditorTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          audio: { technics-out: { match: Technics }, sony-out: { match: Sony } }
          bluetooth: { technics-bt: { address: "B8:20:8E:ED:48:FC", name: Technics }, sony-bt: { address: "AC:80:0A:11:22:33", name: Sony } }
          bound:
            headphones: { bluetooth: technics-bt, audio: technics-out, name: Technics headphones }
            sony: { bluetooth: sony-bt, audio: sony-out, name: Sony headphones }
        flows:
          f:
            name: Headphones
            inputs:
              headphones: { kind: bound, name: Headphones, default: headphones }
              output: { kind: audio, name: Output, default: technics-out }
            nodes:
              - { id: connect, step: bluetooth.connect, with: { device: $headphones } }
              - { id: setDefault, step: audio.setDefault, with: { device: $headphones }, after: [connect] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly FlowCanvasViewModel _canvas;
    private readonly InspectorViewModel _inspector;

    public BoundInputsEditorTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        Assert.DoesNotContain(_session.Problems, p => p.Severity == Severity.Error);
        _engine.Answer = _ => new PipeMessage { Ok = true };
        _canvas = new FlowCanvasViewModel(_session, "f");
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
    public void TheKindListOffersBoundDevice()
    {
        var input = _inspector.Inputs.Single(i => i.Name == "headphones");

        Assert.Contains("Bound device", FlowInputViewModel.KindNames);
        Assert.Equal(LibraryKind.Bound, input.Kind);
        Assert.Equal("Bound device", FlowInputViewModel.KindNames[input.KindIndex]);
    }

    [Fact]
    public void TheDefaultPickerListsOnlyBoundDevices()
    {
        var input = _inspector.Inputs.Single(i => i.Name == "headphones");

        Assert.Equal("headphones", input.Device?.Key);
        Assert.Equal(["headphones", "sony"], input.Devices.Where(d => d.Source == DeviceSource.Library).Select(d => d.Key));
        Assert.All(input.Devices, d => Assert.NotEqual(DeviceSource.Windows, d.Source));

        input.Device = input.Devices.Single(d => d.Key == "sony");

        Assert.Equal("sony", _canvas.Flow!.Inputs["headphones"].Default);
    }

    [Fact]
    public void ChangingAnInputToBoundPicksTheFirstBoundDevice()
    {
        var output = _inspector.Inputs.Single(i => i.Name == "output");

        output.KindIndex = Array.IndexOf(FlowInputViewModel.KindNames.ToArray(), "Bound device");

        Assert.Equal(new FlowInput(LibraryKind.Bound, "Output", "headphones"), _canvas.Flow!.Inputs["output"]);
    }

    [Fact]
    public void BluetoothAndAudioPickersOfferTheBoundInput()
    {
        foreach (var step in new[] { "connect", "setDefault" })
        {
            _canvas.Select(step);
            var field = Field("device");
            Assert.Equal(DeviceSource.Input, field.Device?.Source);
            Assert.Equal("$headphones", field.Device?.Key);
            Assert.Equal("Technics headphones unless the caller picks another", field.Device?.Caption);
        }
        // The audio picker also offers the audio input; the Bluetooth one doesn't.
        Assert.Contains(Field("device").Devices, d => d.Key == "$output");
        _canvas.Select("connect");
        Assert.DoesNotContain(Field("device").Devices, d => d.Key == "$output");
    }

    [Fact]
    public void ADeletedDefaultShowsAsMissingWithTheInputsError()
    {
        _session.Update(c => c with { Flows = [c.Flows[0] with { Inputs = new(c.Flows[0].Inputs.Select(i => i.Key == "headphones" ? KeyValuePair.Create(i.Key, i.Value with { Default = "gone" }) : i)) }] });

        var input = _inspector.Inputs.Single(i => i.Name == "headphones");
        Assert.Equal(DeviceSource.Missing, input.Device?.Source);
        Assert.Equal("There's no bound device called \"gone\" in your Library.", input.Error);
    }
}
