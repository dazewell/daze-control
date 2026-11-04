using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class InspectorViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly InspectorViewModel _inspector;

    public InspectorViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
        _engine.Answer = request => request.Op switch
        {
            Ops.Devices when request.Kind == "bluetooth" => new PipeMessage
            {
                Ok = true,
                Devices =
                [
                    new DeviceInfo { Name = "B8:20:8E:ED:48:FC", Detail = "Technics", State = "connected" },
                    new DeviceInfo { Name = "11:22:33:44:55:66", Detail = "WH-1000XM4", State = "not connected" },
                    new DeviceInfo { Name = "D9:E0:8E:C2:96:85", Detail = "MX Master 3S", State = "connected", NotConnectable = true },
                ],
            },
            _ => new PipeMessage { Ok = true },
        };
        _inspector = new InspectorViewModel(_session, _canvas, _engine, action => action(), _files.Contains);
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

    [Fact]
    public void NothingSelectedShowsTheFlowsSettings()
    {
        Assert.Equal(InspectorMode.Flow, _inspector.Mode);
        Assert.Equal("Connect Technics", _inspector.FlowName);
        Assert.True(_inspector.ShowPopup);
    }

    [Fact]
    public void AStepShowsItsHeaderAndOneFieldPerParam()
    {
        _canvas.Select("vol");

        Assert.Equal(InspectorMode.Step, _inspector.Mode);
        Assert.Equal("Audio › Set volume", _inspector.Kind);
        Assert.Equal("Set volume", _inspector.NamePlaceholder);
        Assert.Equal(["device", "percent"], _inspector.Fields.Select(f => f.Name));
        Assert.Equal(30, Field("percent").Number);
        Assert.True(Field("device").IsDevice);
        Assert.Equal(3, _inspector.Retries);
        Assert.Equal(1, _inspector.RetryDelay);
        Assert.Equal(5, _inspector.Timeout);
        Assert.Equal("10 s", _inspector.TimeoutPlaceholder);
    }

    [Fact]
    public void EditingAFieldWritesTheParamAsOneUndoStep()
    {
        _canvas.Select("vol");

        Field("percent").Number = 55;

        Assert.Equal(new ScalarParam("55"), Step("vol").With["percent"]);
        _canvas.Undo();
        Assert.Equal(new ScalarParam("30"), Step("vol").With["percent"]);
        Assert.Equal(30, Field("percent").Number);
    }

    [Fact]
    public void NumbersAreClampedToTheParamsRange()
    {
        _canvas.Select("vol");

        Field("percent").Number = 250;

        Assert.Equal(new ScalarParam("100"), Step("vol").With["percent"]);
    }

    [Fact]
    public void DurationsAreEditedInSeconds()
    {
        _canvas.Select("w1");

        Field("duration").Number = 1.5;

        Assert.Equal(new ScalarParam("1s500ms"), Step("w1").With["duration"]);
    }

    [Fact]
    public void ADefaultValueIsLeftOutOfTheYaml()
    {
        _canvas.Select("out");
        Field("roles").Choice = "console";
        Assert.Equal(new ScalarParam("console"), Step("out").With["roles"]);

        Field("roles").Choice = "all";

        Assert.False(Step("out").With.ContainsKey("roles"));
    }

    [Fact]
    public void ListsAreOnePerLine()
    {
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, new StepNode("run", "process.run") { With = new([new("path", (ParamValue)"notepad.exe")]) }] });
        _canvas.Select("run");

        Field("args").Text = "/a\r\n\r\n  b c \n";

        Assert.Equal(new ListParam(["/a", "b c"]), Step("run").With["args"]);
        Assert.Null(Field("path").Error); // a bare name is found on PATH at run time
    }

    [Fact]
    public void AMissingFullPathSaysSo()
    {
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, new StepNode("run", "process.run") { With = new([new("path", (ParamValue)"notepad.exe")]) }] });
        _canvas.Select("run");

        Field("path").Text = @"C:\nope\app.exe";
        Assert.Equal("Can't find this file.", Field("path").Error);

        _files.Add(@"C:\yes\app.exe");
        Field("path").Text = @"C:\yes\app.exe";
        Assert.Null(Field("path").Error);
    }

    [Fact]
    public void ARequiredFieldShowsItsProblem()
    {
        _canvas.Select("note");

        Field("text").Text = "";

        Assert.NotNull(Field("text").Error);
        Assert.Equal("Message. Fill this in.", Field("text").AutomationName);
    }

    [Fact]
    public void DevicePickerListsLibraryFirstThenWindowsWithLiveState()
    {
        _canvas.Select("bt");

        var options = Field("device").Devices;
        Assert.Equal([DeviceSource.Library, DeviceSource.Windows, DeviceSource.Windows], options.Select(o => o.Source));
        Assert.Equal("Technics", options[0].Title);
        Assert.Equal("Connected", options[0].Caption);
        Assert.Equal("WH-1000XM4", options[1].Title);
        Assert.Equal("From Windows · Not connected", options[1].Caption);
        Assert.Same(options[0], Field("device").Device);
    }

    [Fact]
    public void ABluetoothDeviceWithoutAudioIsListedButCantBePicked()
    {
        _canvas.Select("bt");
        var mouse = Field("device").Devices.Single(o => o.Title == "MX Master 3S");

        Assert.False(mouse.IsEnabled);
        Assert.Equal(LibraryDevices.NotConnectable, mouse.Caption);
        Field("device").Device = mouse;

        Assert.DoesNotContain(_session.Current.Library.Bluetooth, d => d.Address == "D9:E0:8E:C2:96:85");
        Assert.Equal("Technics", Field("device").Device!.Title);
    }

    [Fact]
    public void ALibraryDeviceWithoutAudioIsShownButCantBePicked()
    {
        var library = new Library { Bluetooth = [new BluetoothDevice("technics", "B8:20:8E:ED:48:FC", "Technics"),
            new BluetoothDevice("mouse", "D9:E0:8E:C2:96:85", "MX Master 3S")] };
        var live = new List<DeviceInfo> { new() { Name = "D9:E0:8E:C2:96:85", Detail = "MX Master 3S", NotConnectable = true } };

        var mouse = LibraryDevices.Options(LibraryKind.Bluetooth, library, live, "technics", required: true, canCapture: false)
            .Single(o => o.Key == "mouse");

        Assert.False(mouse.IsEnabled);
        Assert.Equal(LibraryDevices.NotConnectable, mouse.Caption);
    }

    [Fact]
    public void PickingAWindowsDeviceAddsItToTheLibrarySilently()
    {
        _canvas.Select("bt");

        Field("device").Device = Field("device").Devices.Single(o => o.Title == "WH-1000XM4");

        var added = Assert.Single(_session.Current.Library.Bluetooth, d => d.Address == "11:22:33:44:55:66");
        Assert.Equal("wh-1000xm4", added.Key);
        Assert.Equal("WH-1000XM4", added.Name);
        Assert.Equal(new ScalarParam("wh-1000xm4"), Step("bt").With["device"]);
        Assert.Equal(DeviceSource.Library, Field("device").Device!.Source);
    }

    [Fact]
    public void AMissingRefStaysSelectedAsNotFound()
    {
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes.Select(n => n.Id == "bt" ? ((StepNode)n) with { With = new([new("device", (ParamValue)"gone")]) } : n)] });
        _canvas.Select("bt");

        Assert.Equal(DeviceSource.Missing, Field("device").Device!.Source);
        Assert.Equal(LibraryDevices.NotFound, Field("device").Device!.Caption);
        Assert.NotNull(Field("device").Error);
    }

    [Fact]
    public void AnOptionalDeviceCanBeTheDefault()
    {
        _canvas.Select("vol");

        Assert.Equal(DeviceSource.None, Field("device").Device!.Source);
        Field("device").Device = Field("device").Devices.First(o => o.Source == DeviceSource.Library);
        Assert.Equal(new ScalarParam("technics"), Step("vol").With["device"]);

        Field("device").Device = Field("device").Devices.First(o => o.Source == DeviceSource.None);
        Assert.False(Step("vol").With.ContainsKey("device"));
    }

    [Fact]
    public void NameTimingAndPolicyEditTheStep()
    {
        _canvas.Select("vol");

        _inspector.Name = "  Quieter  ";
        _inspector.PolicyIndex = 1;
        _inspector.Retries = 0;
        _inspector.Timeout = double.NaN;

        var step = Step("vol");
        Assert.Equal("Quieter", step.Name);
        Assert.Equal(FailurePolicy.Skip, step.OnFailure);
        Assert.Null(step.Retry);
        Assert.Null(step.Timeout);
        _inspector.Name = "";
        Assert.Null(Step("vol").Name);
    }

    [Fact]
    public void ACardShowsItsNameAndPolicyOnly()
    {
        _canvas.Select("both");

        Assert.Equal(InspectorMode.Card, _inspector.Mode);
        Assert.Equal("Runs together · 2 steps", _inspector.Kind);
        Assert.Empty(_inspector.Fields);
        _inspector.PolicyIndex = 2;
        Assert.Equal(FailurePolicy.Ignore, _canvas.Flow!.FindNode("both")!.OnFailure);
    }

    [Fact]
    public void SeveralSelectedSaySo()
    {
        _canvas.SelectOnly(["bt", "vol"]);

        Assert.Equal(InspectorMode.Many, _inspector.Mode);
        Assert.Equal("2 selected", _inspector.ManyText);
    }

    [Theory]
    [InlineData(FailurePolicy.Stop, "out,vol,note,both,w1,w2")]
    [InlineData(FailurePolicy.Skip, "out,vol,note")]
    [InlineData(FailurePolicy.Ignore, "")]
    public void PolicyPreviewDimsWhatWouldntRun(FailurePolicy policy, string dimmed)
    {
        Assert.Equal(dimmed.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(),
            InspectorViewModel.Affected(_canvas.Flow!, "bt", policy).Order());
    }

    [Fact]
    public void SkipPreviewSparesTheFallback()
    {
        // note waits for out with "if it fails": it runs when out fails, so it isn't dimmed.
        Assert.Empty(InspectorViewModel.Affected(_canvas.Flow!, "out", FailurePolicy.Skip));
    }

    [Fact]
    public void HoveringAPolicyDimsTheCanvasAndLeavingClearsIt()
    {
        _canvas.Select("bt");

        _inspector.PreviewPolicy(1);
        Assert.True(_canvas.Find("vol")!.IsDimmed);
        Assert.False(_canvas.Find("bt")!.IsDimmed);

        _inspector.PreviewPolicy(null);
        Assert.DoesNotContain(_canvas.Nodes, n => n.IsDimmed);
    }

    [Fact]
    public async Task TestSendsTheStepWithTheValuesOnScreen()
    {
        _engine.Answer = request => new PipeMessage
        {
            Ok = true,
            Run = new RunRecord(Guid.NewGuid(), "test:f", "Connect Technics", "Test", DateTimeOffset.Now, TimeSpan.FromSeconds(1.2),
                FlowOutcome.Succeeded, [new NodeResult("vol", NodeOutcome.Succeeded, Duration: TimeSpan.FromSeconds(1.2))]),
        };
        _canvas.Select("vol");
        Field("percent").Number = 42;

        await _inspector.TestCommand.ExecuteAsync(null);

        var request = Assert.Single(_engine.Requests, r => r.Op == Ops.TestStep);
        Assert.Equal("f", request.Flow);
        Assert.Equal("vol", request.Node);
        var sent = ConfigYaml.Load(request.Config!).Config!;
        Assert.Equal(new ScalarParam("42"), ((StepNode)sent.Flows[0].FindNode("vol")!).With["percent"]);
        Assert.Equal("Worked in 1.2 s.", _inspector.TestResult);
        Assert.True(_inspector.TestSucceeded);
    }

    [Fact]
    public async Task AFailedTestShowsTheFirstLineAndTheRestAsDetails()
    {
        _engine.Answer = request => new PipeMessage
        {
            Ok = true,
            Run = new RunRecord(Guid.NewGuid(), "test:f", "x", "Test", DateTimeOffset.Now, TimeSpan.Zero, FlowOutcome.Failed,
                [new NodeResult("bt", NodeOutcome.Failed, Error: "Windows couldn't find Technics.\nHRESULT 0x80070490")]),
        };
        _canvas.Select("bt");

        await _inspector.TestCommand.ExecuteAsync(null);

        Assert.Equal("Didn't work: Windows couldn't find Technics.", _inspector.TestResult);
        Assert.False(_inspector.TestSucceeded);
        Assert.Contains("HRESULT", _inspector.TestDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void TestNeedsTheEngine()
    {
        _canvas.Select("vol");
        Assert.True(_inspector.CanTest);

        _engine.SetRunning(false);

        Assert.False(_inspector.CanTest);
    }

    [Fact]
    public void FlowSettingsEditTheFlow()
    {
        _inspector.FlowName = "Technics";
        _inspector.TrayFolder = "Audio";
        _inspector.LineAbove = true;
        _inspector.WhenRunningIndex = 2;
        _inspector.ShowPopup = false;
        _inspector.PickGlyphCommand.Execute(_inspector.GlyphOptions.First(g => g.Code == "E7FC"));

        var flow = _canvas.Flow!;
        Assert.Equal("Technics", flow.Name);
        Assert.Equal("Audio", flow.Tray.Folder);
        Assert.True(flow.Tray.Separator);
        Assert.Equal(WhenRunning.Restart, flow.WhenRunning);
        Assert.False(flow.Popup);
        Assert.Equal("E7FC", flow.Glyph);
    }

    [Fact]
    public void ShortcutsAreRecordedAddedAndRemoved()
    {
        _inspector.AddShortcutCommand.Execute(null);
        Assert.Equal([""], _inspector.Shortcuts.Select(s => s.Hotkey));
        Assert.Empty(_canvas.Flow!.Hotkeys); // nothing written until recorded

        _inspector.SetShortcut(0, "Alt+T");
        Assert.Equal(["Alt+T"], _canvas.Flow!.Hotkeys);
        Assert.Equal(["Alt+T"], _inspector.Shortcuts.Select(s => s.Hotkey));

        _inspector.RemoveShortcutCommand.Execute(_inspector.Shortcuts[0]);
        Assert.Empty(_canvas.Flow!.Hotkeys);
    }

    [Fact]
    public void AShortcutConflictShowsNextToIt()
    {
        _session.Update(c => c with { Flows = [new Flow("g", "Other", [new StepNode("a", "notify") { With = new([new("text", (ParamValue)"x")]) }]) { Hotkeys = ["Alt+T"] }, .. c.Flows] });

        _inspector.SetShortcut(0, "Alt+T");

        Assert.Equal("Alt+T already runs flow \"g\".", _inspector.Shortcuts[0].Problem);
    }

    [Fact]
    public void StopWithAFallbackEdgeDimsOnlyWhatWaitsForItToWork()
    {
        // out has an "if it fails" edge to note: failing doesn't stop the flow, note still runs.
        Assert.Empty(InspectorViewModel.Affected(_canvas.Flow!, "out", FailurePolicy.Stop));
    }

    [Fact]
    public void SkipStopsAtWhateverHappensEdgesBelowTheFailedStep()
    {
        var flow = new Flow("g", "G",
        [
            new StepNode("a", "notify"),
            new StepNode("b", "notify") { After = [new Edge("a")] },
            new StepNode("c", "notify") { After = [new Edge("b", EdgeWhen.Always)] },
        ]);

        Assert.Equal(["b"], InspectorViewModel.Affected(flow, "a", FailurePolicy.Skip));
    }

    [Fact]
    public void AMemberFailsIntoItsCardWhosePolicyDecides()
    {
        var flow = new Flow("g", "G",
        [
            new GroupNode("card", ["m1", "m2"]) { OnFailure = FailurePolicy.Ignore },
            new StepNode("m1", "notify"),
            new StepNode("m2", "notify"),
            new StepNode("after", "notify") { After = [new Edge("card")] },
        ]);

        Assert.Empty(InspectorViewModel.Affected(flow, "m1", FailurePolicy.Stop));
        var stopping = flow with { Nodes = [.. flow.Nodes.Select(n => n.Id == "card" ? n with { OnFailure = FailurePolicy.Stop } : n)] };
        Assert.Equal(["after"], InspectorViewModel.Affected(stopping, "m1", FailurePolicy.Skip));
        Assert.Empty(InspectorViewModel.Affected(stopping, "m1", FailurePolicy.Ignore));
    }

    [Fact]
    public void ANewLayoutStepSelectsNothingSoCaptureCanBePicked()
    {
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, new StepNode("lay", "display.layout")] });
        _canvas.Select("lay");

        var field = Field("layout");
        Assert.Null(field.Device);
        Assert.Contains(field.Devices, o => o.Source == DeviceSource.Capture);
    }

    [Fact]
    public void CaptureOptionAppearsWhenTheEngineStarts()
    {
        _engine.SetRunning(false);
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, new StepNode("lay", "display.layout")] });
        _canvas.Select("lay");
        Assert.DoesNotContain(Field("layout").Devices, o => o.Source == DeviceSource.Capture);

        _engine.SetRunning(true);

        Assert.Contains(Field("layout").Devices, o => o.Source == DeviceSource.Capture);
    }

    // ---- "If it fails, run…" and Connections (second-round feedback) ----

    [Fact]
    public void IfItFailsRunAddsAStepThatRunsOnlyOnFailure()
    {
        _canvas.Select("vol");
        var id = _inspector.AddFallback("notify");

        Assert.NotNull(id);
        Assert.Equal(EdgeWhen.Failure, Step(id!).After.Single(e => e.From == "vol").When);
        Assert.Equal([id!], _canvas.SelectedIds);
        Assert.Equal("If it fails", _canvas.Edges.Single(e => e.To == id).Chip);
        _canvas.Undo(); // one gesture, one undo step
        Assert.Null(_canvas.Flow!.FindNode(id!));
    }

    [Fact]
    public void IfItFailsRunLinksAStepAlreadyInTheFlow()
    {
        _canvas.Select("vol");
        Assert.Contains(_inspector.FallbackTargets, t => t.Id == "both");
        Assert.DoesNotContain(_inspector.FallbackTargets, t => t.Id == "bt"); // vol waits for it: a loop
        Assert.DoesNotContain(_inspector.FallbackTargets, t => t.Id == "vol");

        Assert.Equal(ConnectRefusal.None, _inspector.LinkFallback("both"));
        Assert.Equal(EdgeWhen.Failure, _canvas.Edges.Single(e => e.From == "vol" && e.To == "both").When);
        Assert.Equal(ConnectRefusal.Loop, _inspector.LinkFallback("bt"));
    }

    [Fact]
    public void IfItFailsRunTurnsAnExistingEdgeIntoAFallback()
    {
        _canvas.Select("bt");
        Assert.Contains(_inspector.FallbackTargets, t => t.Id == "vol");
        Assert.Equal(ConnectRefusal.None, _inspector.LinkFallback("vol"));
        Assert.Equal(EdgeWhen.Failure, Step("vol").After.Single().When);
        Assert.DoesNotContain(_inspector.FallbackTargets, t => t.Id == "vol");
    }

    [Fact]
    public void ConnectionsListTheEdgesAndChangeTheirCondition()
    {
        _canvas.Select("out");
        Assert.Equal(["Runs after Connect", "Show a message runs after this"], _inspector.Connections.Select(c => c.Text));
        var fallback = _inspector.Connections[1];
        Assert.Equal(1, fallback.WhenIndex);

        fallback.WhenIndex = 2; // "Whatever happens"
        Assert.Equal(EdgeWhen.Always, Step("note").After.Single().When);

        _inspector.Connections[0].WhenIndex = 1;
        Assert.Equal(EdgeWhen.Failure, Step("out").After.Single().When);
    }

    // ---- F2: "Run a flow" picks a flow by name and stores its id ----

    private static Flow NotifyFlow(string id, string name) =>
        new(id, name, [new StepNode("a", "notify") { With = new([new("text", (ParamValue)"x")]) }]);

    private void AddRunStep(string? target)
    {
        var step = new StepNode("call", Core.Catalog.ProviderCatalog.RunFlowId);
        if (target is not null)
            step = step with { With = new([new("flow", (ParamValue)target)]) };
        _canvas.Edit(f => f with { Nodes = [.. f.Nodes, step] });
        _canvas.Select("call");
    }

    [Fact]
    public void RunAFlowIsListedUnderFlows()
    {
        AddRunStep(null);

        Assert.Equal("Flows › Run a flow", _inspector.Kind);
    }

    [Fact]
    public void EveryStepAreaHasATitleStartingWithACapital()
    {
        foreach (var area in Core.Catalog.ProviderCatalog.All.Select(d => d.Area).Distinct())
        {
            var title = CanvasNodeViewModel.Area(area);
            Assert.True(title.Length > 0 && char.IsUpper(title[0]), $"{area} → {title}");
        }
    }

    [Fact]
    public void TheFlowPickerListsTheOtherFlowsByNameAndStoresTheId()
    {
        _session.Update(c => c with { Flows = [.. c.Flows, NotifyFlow("night", "Night"), NotifyFlow("morning", "Morning") with { Enabled = false }] });
        AddRunStep(null);

        var field = Field("flow");
        Assert.True(field.IsDevice);
        Assert.False(field.IsSuggestText);
        Assert.Equal(["Morning", "Night"], field.Devices.Select(d => d.Title));
        Assert.Equal(["morning", "night"], field.Devices.Select(d => d.Key));
        Assert.Equal("Turned off", field.Devices[0].Caption);
        Assert.Equal("", field.Devices[1].Caption);
        Assert.Equal("Choose a flow.", field.Error);

        field.Device = field.Devices[1];

        Assert.Equal("night", ((ScalarParam)Step("call").With["flow"]).Text);
        Assert.Equal("Night", Field("flow").Device?.Title);
        Assert.Equal("Night", _canvas.Nodes.Single(n => n.Id == "call").Summary); // the card shows the name, never the id
        Assert.Null(Field("flow").Error);
        Assert.DoesNotContain(_session.Problems, p => p.Path.StartsWith("flows.f.nodes.call", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoOtherFlowThePickerSaysToMakeOneFirst()
    {
        AddRunStep(null);

        var field = Field("flow");
        Assert.Empty(field.Devices);
        Assert.Equal(InspectorViewModel.NoOtherFlow, field.SuggestionHint);
    }

    [Fact]
    public void AFlowThatIsGoneStaysSelectedAndSaysSo()
    {
        _session.Update(c => c with { Flows = [.. c.Flows, NotifyFlow("night", "Night")] });
        AddRunStep("gone");

        var field = Field("flow");
        Assert.Equal(DeviceSource.Missing, field.Device?.Source);
        Assert.Equal("gone", field.Device?.Title);
        Assert.Equal("Isn't there anymore", field.Device?.Caption);
        Assert.Equal(["Night", "gone"], field.Devices.Select(d => d.Title));
        Assert.Contains("isn't there anymore", field.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void APaddedIdStillFindsItsFlow()
    {
        _session.Update(c => c with { Flows = [.. c.Flows, NotifyFlow("night", "Night")] });
        AddRunStep(" night ");

        Assert.Equal(DeviceSource.Library, Field("flow").Device?.Source);
        Assert.Equal("Night", Field("flow").Device?.Title);
        Assert.Equal("Night", _canvas.Nodes.Single(n => n.Id == "call").Summary);
    }

    [Fact]
    public void OnlyAGoneFlowStillSaysToMakeAnotherOne()
    {
        AddRunStep("gone");

        Assert.Equal("gone", Field("flow").Device?.Title);
        Assert.Equal(InspectorViewModel.NoOtherFlow, Field("flow").SuggestionHint);
    }

    [Fact]
    public void AFlowWithProblemsSaysItNeedsFixing()
    {
        _session.Update(c => c with { Flows = [.. c.Flows, new Flow("bad", "Bad", [new StepNode("a", "notify")])] });
        AddRunStep(null);

        Assert.Equal("Needs fixing", Field("flow").Devices.Single(d => d.Key == "bad").Caption);
    }
}
