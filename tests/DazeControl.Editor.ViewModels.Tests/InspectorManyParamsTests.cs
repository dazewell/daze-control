using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T15.3: the params several selected steps share are edited together, as one Undo (D-084, DESIGN D2/D3).</summary>
public sealed class InspectorManyParamsTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          audio: { technics: { match: "Technics EAH-AZ80" }, desk: { match: "Desk speakers" } }
          bluetooth: { technics: { address: "B8:20:8E:ED:48:FC", name: "Technics" } }
        flows:
          f:
            name: Many
            nodes:
              - { id: a1, step: audio.setDefault, with: { device: technics } }
              - { id: a2, step: audio.setDefault, with: { device: desk } }
              - { id: v1, step: audio.setVolume, with: { percent: "30", device: technics } }
              - { id: v2, step: audio.setVolume, with: { percent: "30", device: technics } }
              - { id: b1, step: bluetooth.connect, with: { device: technics } }
              - { id: n1, step: notify, with: { text: Done } }
              - { id: n2, step: notify, with: { text: Done } }
              - { id: m1, step: audio.mute }
              - { id: m2, step: audio.mute, with: { muted: "false" } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly InspectorViewModel _inspector;

    public InspectorManyParamsTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
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

    private ParamValue? With(string id, string name) => ((StepNode)_canvas.Flow!.FindNode(id)!).With.TryGetValue(name, out var v) ? v : null;

    private ParamFieldViewModel Field(string name) => _inspector.ManyFields.Single(f => f.Name == name);

    [Fact]
    public void ADevicePickedForAllIsWrittenToEveryStepAsOneUndo()
    {
        _canvas.SelectOnly(["a1", "a2"]);
        var device = Field("device");
        Assert.True(device.IsMixed);
        Assert.Null(device.Device);
        Assert.Equal("Device, mixed", device.AutomationName);
        Assert.Equal(["device", "roles"], _inspector.ManyFields.Select(f => f.Name));

        device.Device = device.Devices.First(o => o.Key == "technics");

        Assert.Equal("technics", ((ScalarParam)With("a2", "device")!).Text);
        Assert.False(Field("device").IsMixed);
        _canvas.Undo();
        Assert.Equal("desk", ((ScalarParam)With("a2", "device")!).Text);
        Assert.Equal("technics", ((ScalarParam)With("a1", "device")!).Text);
        Assert.True(Field("device").IsMixed);
    }

    [Fact]
    public void ATextAndABooleanApplyToAll()
    {
        _canvas.SelectOnly(["n1", "n2"]);
        Assert.False(Field("text").IsMixed);
        Assert.Equal("Done", Field("text").Text);
        Field("text").Text = "Hi";
        Assert.Equal("Hi", ((ScalarParam)With("n1", "text")!).Text);
        Assert.Equal("Hi", ((ScalarParam)With("n2", "text")!).Text);

        _canvas.SelectOnly(["m1", "m2"]);
        Assert.True(Field("muted").IsMixed);
        Field("muted").IsOn = false;
        Assert.False(Field("muted").IsMixed);
        Assert.Equal("false", ((ScalarParam)With("m1", "muted")!).Text);
    }

    [Fact]
    public void ARequiredParamCannotBeClearedButAnOptionalOneClearsForAll()
    {
        _canvas.SelectOnly(["v1", "v2"]);
        Field("percent").Number = double.NaN;
        Assert.True(_inspector.HasManyProblem);
        Assert.Equal("30", ((ScalarParam)With("v1", "percent")!).Text);
        Assert.Equal("30", ((ScalarParam)With("v2", "percent")!).Text);

        var device = Field("device");
        device.Device = device.Devices.First(o => o.Source == DeviceSource.None);
        Assert.Null(With("v1", "device"));
        Assert.Null(With("v2", "device"));
        Assert.False(_inspector.HasManyProblem);
    }

    [Fact]
    public void DevicesOfDifferentLibraryKindsAreNotShared()
    {
        _canvas.SelectOnly(["a1", "b1"]);
        Assert.Empty(_inspector.ManyFields);
        Assert.False(_inspector.ManyHasFields);

        _canvas.SelectOnly(["a1", "v1"]); // a required and an optional device differ in meaning: not shared
        Assert.Empty(_inspector.ManyFields);

        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, new StepNode("k1", "audio.check")] });
        _canvas.SelectOnly(["a1", "k1"]); // two audio types with the same required device: shared; "Use for" / "Is" are not
        Assert.Equal(["device"], _inspector.ManyFields.Select(f => f.Name));

        _canvas.SelectOnly(["a1", "n1"]);
        Assert.Empty(_inspector.ManyFields);
    }

    [Fact]
    public void ParamsWithDifferentChoicesAreNotShared()
    {
        // audio.check and bluetooth.check both have "is", with different choices (and different library kinds for device).
        _canvas.Edit(f => f with
        {
            Nodes = [.. f.Nodes,
                new StepNode("c1", "audio.check"),
                new StepNode("c2", "bluetooth.check")],
        });
        _canvas.SelectOnly(["c1", "c2"]);
        Assert.DoesNotContain(_inspector.ManyFields, f => f.Name == "is");
    }
}
