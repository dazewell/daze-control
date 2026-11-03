using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>The inspector's suggestion controls (D-050, D-051, T11.3).</summary>
public sealed class PickerTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          android: { tv: { address: 192.168.1.50, inputs: { hdmi2: [down, down], tv: [up] } } }
        flows:
          f:
            name: Living room
            nodes:
              - { id: screens, step: display.outputs, with: { outputs: [DELL S2722QC, Old TV] } }
              - { id: keys, step: android.keys, with: { device: tv, keys: [home, back] }, after: [screens] }
              - { id: input, step: android.input, with: { device: tv, input: hdmi2 }, after: [screens] }
              - { id: ps, step: script.powershell, with: { script: "Get-Date" }, after: [screens] }
              - { id: svc, step: service.start, with: { name: Spooler }, after: [screens] }
              - { id: flag, step: flag.set, with: { name: gaming }, after: [screens] }
          g:
            name: Other
            nodes:
              - { id: check, step: flag.check, with: { name: gaming } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly InspectorViewModel _inspector;

    private DeviceInfo[] _screens =
    [
        new() { Name = "DP-1", Detail = "DELL S2722QC, on", State = "on", Value = "DELL S2722QC" },
        new() { Name = "HDMI-1", Detail = "LG TV, off", State = "off", Value = "LG TV" },
    ];

    public PickerTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
        _engine.Answer = request => (request.Op, request.Kind) switch
        {
            (Ops.Devices, "displays") => new PipeMessage { Ok = true, Devices = _screens },
            (Ops.Devices, "services") => new PipeMessage
            {
                Ok = true,
                Devices = [new DeviceInfo { Name = "Spooler", Detail = "Print Spooler", State = "running", Value = "Spooler" }],
            },
            _ => new PipeMessage { Ok = true },
        };
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

    private IReadOnlyList<string> Items(string step, string param) =>
        Step(step).With.TryGetValue(param, out var value) && value is ListParam list
            ? [.. list.Items.OfType<ScalarParam>().Select(i => i.Text)]
            : [];

    [Fact]
    public void ScreensChecklistShowsThePcsScreensAndKeepsAnUnpluggedOneChecked()
    {
        _canvas.Select("screens");
        var field = Field("outputs");

        Assert.True(field.IsChecklist);
        Assert.False(field.IsMultiline);
        Assert.Equal(["DELL S2722QC", "LG TV", "Old TV"], field.Screens.Select(s => s.Title));
        Assert.Equal([true, false, true], field.Screens.Select(s => s.IsChecked));
        Assert.Equal("DP-1 · On now", field.Screens[0].Caption);
        Assert.Equal(ScreenChoices.NotConnected, field.Screens[2].Caption);
        Assert.Equal("Old TV, " + ScreenChoices.NotConnected, field.Screens[2].AutomationName);
    }

    [Fact]
    public void CheckingAScreenWritesItsMonitorName()
    {
        _canvas.Select("screens");

        Field("outputs").Screens.Single(s => s.Title == "LG TV").IsChecked = true;

        Assert.Equal(["DELL S2722QC", "LG TV", "Old TV"], Items("screens", "outputs"));
    }

    [Fact]
    public void UncheckingAnUnpluggedScreenDropsIt()
    {
        _canvas.Select("screens");

        Field("outputs").Screens.Single(s => s.Title == "Old TV").IsChecked = false;

        Assert.Equal(["DELL S2722QC"], Items("screens", "outputs"));
    }

    [Fact]
    public void TwinScreensAreWrittenByPort()
    {
        _screens =
        [
            new() { Name = "DP-1", State = "on", Value = "DELL U2723QE" },
            new() { Name = "DP-2", State = "off", Value = "DELL U2723QE" },
        ];
        _canvas.Select("screens");

        Field("outputs").Screens.Single(s => s.Caption.StartsWith("DP-2", StringComparison.Ordinal)).IsChecked = true;

        Assert.Contains("DP-2", Items("screens", "outputs"));
    }

    [Fact]
    public void AScreenWhoseNameIsInsideAnothersIsStillWrittenByName()
    {
        _screens =
        [
            new() { Name = "DP-1", State = "on", Value = "DELL U2723QE HDR" },
            new() { Name = "DP-2", State = "on", Value = "DELL U2723QE" },
        ];
        _canvas.Select("screens");

        Field("outputs").Screens.Single(s => s.Title == "DELL U2723QE").IsChecked = true;

        // Exact names match first (D-051), so the shorter name can't turn on DP-1.
        Assert.Contains("DELL U2723QE", Items("screens", "outputs"));
    }

    [Fact]
    public void UseTheScreensThatAreOnNowChecksExactlyThose()
    {
        _canvas.Select("screens");

        Field("outputs").UseScreensOnNow();

        Assert.Equal(["DELL S2722QC"], Items("screens", "outputs"));
        Assert.Equal([true, false], Field("outputs").Screens.Select(s => s.IsChecked)); // the unplugged entry is gone
    }

    [Fact]
    public void KeysAreTokensInSendOrder()
    {
        _canvas.Select("keys");
        var field = Field("keys");

        Assert.True(field.IsTokenList);
        Assert.Equal(["home, 1 of 2", "back, 2 of 2"], field.Tokens.Select(t => t.AutomationName));

        field.AddToken("dpad_down");
        field.MoveToken(field.Tokens[2], -1);
        field.RemoveToken(field.Tokens[0]);

        Assert.Equal(["dpad_down", "back"], Items("keys", "keys"));
        Assert.Contains("media_play_pause", field.KeySuggestions("play"));
    }

    [Fact]
    public void AndroidInputSuggestsTheChosenDevicesInputs()
    {
        _canvas.Select("input");

        Assert.True(Field("input").IsSuggestText);
        Assert.Equal(["hdmi2", "tv"], Field("input").Suggestions.Select(s => s.Value));
        Assert.False(Field("input").HasSuggestionHint);
    }

    [Fact]
    public void ServicesComeFromTheEngineWithTheirState()
    {
        _canvas.Select("svc");

        var spooler = Assert.Single(Field("name").Suggestions);
        Assert.Equal("Spooler", spooler.ToString());
        Assert.Equal("Print Spooler · Running", spooler.Caption);
    }

    [Fact]
    public void TheSameTextPushedBackChangesNothing()
    {
        _canvas.Select("svc");

        Field("name").Text = "Spooler "; // an editable ComboBox writes its text back on focus

        Assert.False(_canvas.CanUndo);
    }

    [Fact]
    public void FlagsComeFromTheConfig()
    {
        _canvas.Select("flag");

        var gaming = Assert.Single(Field("name").Suggestions);
        Assert.Equal("gaming", gaming.Value);
        Assert.Equal("Used in 2 flows", gaming.Caption);
    }

    [Fact]
    public void AnExampleFillsTheScriptAndUndoBringsTheOldOneBack()
    {
        _canvas.Select("ps");
        var field = Field("script");
        Assert.True(field.HasExamples);

        field.ApplyExample(field.Examples.Single(e => e.Title == "Lock the PC"));
        Assert.Equal(new ScalarParam("rundll32.exe user32.dll,LockWorkStation"), Step("ps").With["script"]);

        _canvas.Undo();
        Assert.Equal(new ScalarParam("Get-Date"), Step("ps").With["script"]);
    }
}
