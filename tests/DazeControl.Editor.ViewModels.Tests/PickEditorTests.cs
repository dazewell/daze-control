using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>D-116: "Result of: &lt;Pick step&gt;" in device pickers, only for earlier Pick steps of the same kind.</summary>
public sealed class PickEditorTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC", name: Technics }
        flows:
          choose:
            name: Choose speakers
            nodes:
              - { id: pick, name: Pick speakers, step: prompt.pick, with: { kind: audio } }
              - { id: set, step: audio.setDefault, with: { device: technics }, after: [pick] }
              - { id: early, step: audio.setDefault, with: { device: sony } }
              - { id: connect, step: bluetooth.connect, with: { device: technics }, after: [set] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly FlowCanvasViewModel _canvas;
    private readonly InspectorViewModel _inspector;

    public PickEditorTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.DoesNotContain(_session.Problems, p => p.Severity == Severity.Error);
        _engine.Answer = _ => new PipeMessage { Ok = true };
        _canvas = new FlowCanvasViewModel(_session, "choose");
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

    private ParamFieldViewModel Device() => _inspector.Fields.Single(f => f.Name == "device");

    [Fact]
    public void AStepAfterThePickOffersItsResultAndSavesIt()
    {
        _canvas.Select("set");

        var devices = Device().Devices;
        Assert.Equal(LibraryDevices.PicksGroup, devices[0].Title);
        Assert.True(devices[0].IsHeader);
        Assert.Equal(("Result of: Pick speakers", "$result:pick"), (devices[1].Title, devices[1].Key));
        Assert.Equal("Library", devices[2].Title);

        Device().Device = devices[1];

        Assert.Equal(new ScalarParam("$result:pick"), ((StepNode)_canvas.Flow!.FindNode("set")!).With["device"]);
        Assert.Equal("Result of Pick speakers", _canvas.Nodes.Single(n => n.Id == "set").Summary);
        Assert.DoesNotContain(_session.Problems, p => p.Severity == Severity.Error);
    }

    [Theory]
    [InlineData("early")] // doesn't wait for the pick
    [InlineData("connect")] // after it, but needs a Bluetooth device
    public void OtherStepsDontOfferIt(string id)
    {
        _canvas.Select(id);

        Assert.DoesNotContain(Device().Devices, d => d.Title == LibraryDevices.PicksGroup || d.Key == "$result:pick");
    }
}
