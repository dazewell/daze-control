using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;
using CtrlAltDaze.Editor.ViewModels.Inspector;
using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>D-117, D-130: "From earlier steps" in device pickers: Device values of earlier steps that fit the setting's kind.</summary>
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
              - { id: legacy, step: prompt.pick, with: { kind: bluetooth, default: Technics } }
              - { id: keyed, step: prompt.pick, with: { kind: audio, default: sony } }
              - { id: louder, step: audio.setVolume, with: { percent: 20 }, after: [set] }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
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
        Assert.Equal(LibraryDevices.EarlierGroup, devices[0].Title);
        Assert.True(devices[0].IsHeader);
        Assert.Equal(("Result of: Pick speakers", "$result:pick"), (devices[1].Title, devices[1].Key));
        Assert.Equal("Library", devices[2].Title);

        Device().Device = devices[1];

        Assert.Equal(new ScalarParam("$result:pick"), ((StepNode)_canvas.Flow!.FindNode("set")!).With["device"]);
        Assert.Equal("Result of Pick speakers", _canvas.Nodes.Single(n => n.Id == "set").Summary);
        Assert.DoesNotContain(_session.Problems, p => p.Severity == Severity.Error);
    }

    private ParamFieldViewModel Default() => _inspector.Fields.Single(f => f.Name == "default");

    private StepNode Step(string id) => (StepNode)_canvas.Flow!.FindNode(id)!;

    /// <summary>D-128: Default is a picker of the Library items of the Pick's kind, not a text box.</summary>
    [Fact]
    public void TheDefaultIsAPickerOfTheKindsLibraryItems()
    {
        _canvas.Select("keyed");

        Assert.True(Default().IsDevice);
        Assert.False(Default().IsText);
        Assert.Equal([null, "technics", "sony"], Default().Devices.Select(d => d.Key));
        Assert.Equal("No default", Default().Devices[0].Title);
        Assert.Equal("sony", Default().Device?.Key);

        _canvas.Select("legacy");

        Assert.Equal(["No default", "Technics"], Default().Devices.Select(d => d.Title));
    }

    [Fact]
    public void AHandTypedNameShowsAsItsItemAndIsRewrittenOnlyWhenChanged()
    {
        _canvas.Select("legacy");

        Assert.Equal("technics", Default().Device?.Key);
        Assert.Equal(new ScalarParam("Technics"), Step("legacy").With["default"]);
        Assert.DoesNotContain(_session.Problems, p => p.Path.EndsWith("legacy.with.default", StringComparison.Ordinal));

        Default().Device = Default().Devices[0]; // changing it writes: first none, then the item's key
        Default().Device = Default().Devices.Single(d => d.Key == "technics");

        Assert.Equal(new ScalarParam("technics"), Step("legacy").With["default"]);
    }

    [Fact]
    public void NoDefaultClearsTheSetting()
    {
        _canvas.Select("legacy");
        Default().Device = Default().Devices[0]; // No default
        Assert.False(Step("legacy").With.ContainsKey("default"));
    }

    [Fact]
    public void ChangingTheKindDropsADefaultOfTheOldKind()
    {
        _canvas.Select("keyed");

        _inspector.Fields.Single(f => f.Name == "kind").Choice = "display";

        Assert.False(Step("keyed").With.ContainsKey("default"));
    }

    [Fact]
    public void ChangingTheKindKeepsADefaultThatStillFits()
    {
        _canvas.Select("keyed");

        _inspector.Fields.Single(f => f.Name == "kind").Choice = "bound";
        Assert.False(Step("keyed").With.ContainsKey("default")); // sony isn't a bound device

        _canvas.Select("legacy");
        _inspector.Fields.Single(f => f.Name == "kind").Choice = "audio"; // "Technics" is a name, not an audio key: gone
        Assert.False(Step("legacy").With.ContainsKey("default"));
    }

    [Fact]
    public void FromEarlierStepsListsEveryDeviceThatFitsAndSavesTheValue()
    {
        // D-130: the Pick's result and Set default device's Device and Previous default, in flow order.
        _canvas.Select("louder");

        var devices = Device().Devices;
        Assert.Equal(DeviceSource.None, devices[0].Source);
        Assert.Equal(LibraryDevices.EarlierGroup, devices[1].Title);
        Assert.Equal(
            [("Result of: Pick speakers", "$result:pick"), ("Device from: Set default device", "$result:set"),
                ("Previous default from: Set default device", "$result:set.previous")],
            devices.Skip(2).TakeWhile(d => !d.IsHeader).Select(d => (d.Title, d.Key)));
        Assert.Equal("The device that was the default before, if it's in your Library", devices[4].Caption);

        Device().Device = devices[4];

        Assert.Equal(new ScalarParam("$result:set.previous"), ((StepNode)_canvas.Flow!.FindNode("louder")!).With["device"]);
        Assert.Equal("Previous default from Set default device · 20", _canvas.Nodes.Single(n => n.Id == "louder").Summary);
        Assert.DoesNotContain(_session.Problems, p => p.Severity == Severity.Error);
    }

    [Theory]
    [InlineData("early")] // doesn't wait for the pick
    [InlineData("connect")] // after it, but needs a Bluetooth device
    public void OtherStepsDontOfferIt(string id)
    {
        _canvas.Select(id);

        Assert.DoesNotContain(Device().Devices, d => d.Title == LibraryDevices.EarlierGroup || d.Key == "$result:pick");
    }
}
