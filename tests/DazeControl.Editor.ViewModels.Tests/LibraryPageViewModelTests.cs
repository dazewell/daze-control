using DazeControl.Core.Model;
using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Editor.ViewModels.LibraryPage;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class LibraryPageViewModelTests : IDisposable
{
    private const string Monitor = @"\\?\DISPLAY#DELA1F2#5&1a2b3c&0&UID4352";

    internal const string Yaml = """
        version: 1
        library:
          audio:
            technics: { match: "Technics EAH-AZ80" }
            mic: { match: "Yeti", flow: input }
          bluetooth:
            technics: { address: "B8:20:8E:ED:48:FC", name: "Technics" }
            sony: { address: "80:99:E7:2A:AB:A1", name: "Sony" }
          displays:
            desk: { monitors: ['\\?\DISPLAY#DELA1F2#5&1a2b3c&0&UID4352'], ccd: AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= }
          android:
            living-room: { address: 192.168.1.50 }
        flows:
          a:
            name: Connect Technics
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }
              - { id: out, step: audio.setDefault, with: { device: technics }, after: [bt] }
              - { id: vol, step: audio.setVolume, with: { device: technics, percent: "30" }, after: [out] }
              - { id: app, step: process.run, with: { path: 'C:\Tools\tv.exe' } }
          b:
            name: TV
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics } }
              - { id: tv, step: android.input, with: { device: living-room, input: hdmi2 } }
              - { id: app, step: process.run, with: { path: 'c:\tools\TV.exe' } }
              - { id: np, step: process.run, with: { path: notepad.exe } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { @"C:\Tools\tv.exe", @"C:\Windows\notepad.exe" };
    private readonly List<string> _revealed = [];
    private readonly ConfigSession _session;
    private readonly LibraryPageViewModel _page;

    public LibraryPageViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        _engine.Answer = request => request.Op switch
        {
            Ops.Devices when request.Kind == "bluetooth" => new PipeMessage
            {
                Ok = true,
                Devices =
                [
                    new DeviceInfo { Name = "B8:20:8E:ED:48:FC", Detail = "Technics", State = "connected" },
                    new DeviceInfo { Name = "80:99:E7:2A:AB:A1", Detail = "Sony", State = "not connected" },
                    new DeviceInfo { Name = "11:22:33:44:55:66", Detail = "WH-1000XM4", State = "not connected" },
                ],
            },
            Ops.Devices when request.Kind == "audio" => new PipeMessage
            {
                Ok = true,
                Devices = [new DeviceInfo { Name = "Speakers (Technics EAH-AZ80)", Detail = "Output, default" }],
            },
            Ops.Devices when request.Kind == "displays" => new PipeMessage
            {
                Ok = true,
                Devices = [new DeviceInfo { Name = "DP-1", Id = Monitor, Detail = "DELL, on" }],
            },
            Ops.Devices when request.Kind == "adb" => new PipeMessage { Ok = true, Devices = [] },
            _ => new PipeMessage { Ok = true },
        };
        _page = new LibraryPageViewModel(_session, _engine, action => action(), _files.Contains, () => @"C:\Windows;C:\Other", _revealed.Add);
    }

    public void Dispose()
    {
        _page.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private LibraryItemViewModel Item(LibraryKind? kind, string key) => _page.Section(kind).Items.Single(i => i.Key == key);

    [Fact]
    public void SectionsAreTheFiveLibraryKindsInPivotOrder() =>
        Assert.Equal(["Audio", "Bluetooth", "Displays", "Android", "Programs"], _page.Sections.Select(s => s.Title));

    [Fact]
    public void ItemsCountTheFlowsThatUseThem()
    {
        Assert.Equal("Used by 2 flows", Item(LibraryKind.Bluetooth, "technics").UsedByText);
        Assert.Equal("Used by 1 flow", Item(LibraryKind.Audio, "technics").UsedByText); // two steps, one flow
        Assert.Equal("Not used yet", Item(LibraryKind.Bluetooth, "sony").UsedByText);
        Assert.Equal("Used by 1 flow", Item(LibraryKind.Android, "living-room").UsedByText);
        Assert.Equal("Not used yet", Item(LibraryKind.Display, "desk").UsedByText);
    }

    [Fact]
    public void ProgramsAreTheRunPathsOfTheFlowsIgnoringCase()
    {
        var programs = _page.Section(null).Items;

        Assert.Equal(["notepad.exe", "tv.exe"], programs.Select(p => p.Title));
        Assert.Equal("Used by 2 flows", programs.Single(p => p.Title == "tv.exe").UsedByText);
        Assert.Equal(@"C:\Tools", programs.Single(p => p.Title == "tv.exe").Detail);
        Assert.False(programs[0].CanRemove);
    }

    [Fact]
    public void CardsDescribeTheItemAndOnlyUnusedOnesCanBeRemoved()
    {
        Assert.Equal("Input", Item(LibraryKind.Audio, "mic").Detail);
        Assert.Equal("B8:20:8E:ED:48:FC", Item(LibraryKind.Bluetooth, "technics").Detail);
        Assert.Equal("1 screen", Item(LibraryKind.Display, "desk").Detail);
        Assert.False(Item(LibraryKind.Bluetooth, "technics").CanRemove);
        Assert.True(Item(LibraryKind.Bluetooth, "sony").CanRemove);
        // Remove shows on every row of a removable kind; in use, it is off and says why.
        Assert.True(Item(LibraryKind.Bluetooth, "technics").Removable);
        Assert.Equal("Used by 2 flows. Remove it from those flows first.", Item(LibraryKind.Bluetooth, "technics").RemoveTip);
        Assert.Equal("Remove from Library", Item(LibraryKind.Bluetooth, "sony").RemoveTip);
    }

    [Fact]
    public async Task RefreshShowsLiveStateAndWhatWindowsHasThatTheLibraryDoesnt()
    {
        await _page.RefreshAsync();

        Assert.Equal("B8:20:8E:ED:48:FC · Connected", Item(LibraryKind.Bluetooth, "technics").Description);
        Assert.Equal("Output, default", Item(LibraryKind.Audio, "technics").State);
        Assert.Equal(LibraryDevices.NotFound, Item(LibraryKind.Audio, "mic").State);
        var available = Assert.Single(_page.Section(LibraryKind.Bluetooth).Available);
        Assert.Equal("WH-1000XM4", available.Title);
        Assert.Empty(_page.Section(LibraryKind.Display).Available); // layouts are captured, not added
    }

    [Fact]
    public async Task AddFromWindowsShowsABluetoothDeviceWithoutAudioDisabled()
    {
        var answer = _engine.Answer;
        _engine.Answer = request => request.Kind == "bluetooth"
            ? new PipeMessage { Ok = true, Devices = [new DeviceInfo { Name = "D3:0C:5D:7D:60:20", Detail = "MX Keys Mini", NotConnectable = true }] }
            : answer!(request);
        await _page.RefreshAsync();
        var keyboard = Assert.Single(_page.Section(LibraryKind.Bluetooth).Available);

        _page.AddCommand.Execute(keyboard);

        Assert.False(keyboard.IsEnabled);
        Assert.Equal(LibraryDevices.NotConnectable, keyboard.Caption);
        Assert.DoesNotContain(_session.Current.Library.Bluetooth, d => d.Address == "D3:0C:5D:7D:60:20");
    }

    [Fact]
    public async Task AddPutsAWindowsDeviceIntoTheLibrary()
    {
        await _page.RefreshAsync();
        var option = _page.Section(LibraryKind.Bluetooth).Available[0];

        _page.AddCommand.Execute(option);

        var added = Assert.Single(_session.Current.Library.Bluetooth, d => d.Address == "11:22:33:44:55:66");
        Assert.Equal("wh-1000xm4", added.Key);
        Assert.Equal("WH-1000XM4", added.Name);
        Assert.Contains(_page.Section(LibraryKind.Bluetooth).Items, i => i.Key == "wh-1000xm4");
        Assert.Empty(_page.Section(LibraryKind.Bluetooth).Available);
    }

    [Fact]
    public void RemoveDropsOnlyUnusedItems()
    {
        _page.RemoveCommand.Execute(Item(LibraryKind.Bluetooth, "technics"));
        _page.RemoveCommand.Execute(Item(LibraryKind.Bluetooth, "sony"));

        Assert.Equal(["technics"], _session.Current.Library.Bluetooth.Select(d => d.Key));
    }

    [Fact]
    public async Task CaptureAddsTheCurrentLayoutUnderANewKey()
    {
        var layout = _session.Current.Library.Displays[0] with { Key = "layout" };
        var yaml = ConfigYaml.Snippet(layout);
        var answer = _engine.Answer!;
        _engine.Answer = r => r.Op == Ops.CaptureDisplayLayout ? new PipeMessage { Ok = true, Yaml = yaml } : answer(r);

        await _page.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(["desk", "layout"], _session.Current.Library.Displays.Select(d => d.Key));
        Assert.Equal(Ops.CaptureDisplayLayout, _engine.Requests[^1].Op);
    }

    [Fact]
    public async Task CaptureWithoutTheEngineSaysSo()
    {
        _engine.IsRunning = false;

        await _page.CaptureCommand.ExecuteAsync(null);

        Assert.Equal("Start Daze Control to capture the layout.", _page.Section(LibraryKind.Display).Note);
        Assert.Single(_session.Current.Library.Displays);
    }

    [Fact]
    public async Task TestAsksOnlyForTheLiveListAndReportsWhatItFound()
    {
        var technics = Item(LibraryKind.Bluetooth, "technics");
        var sony = Item(LibraryKind.Bluetooth, "sony");

        await technics.TestCommand.ExecuteAsync(null);
        await sony.TestCommand.ExecuteAsync(null);

        Assert.True(technics.Worked);
        Assert.Equal("Found Technics: connected.", technics.ResultMessage);
        Assert.True(sony.Cautioned);
        Assert.Equal("Found Sony. Not connected right now. The flow will still try.", sony.ResultMessage);
        Assert.All(_engine.Requests, r => Assert.Equal(Ops.Devices, r.Op)); // never a step that changes anything
        Assert.Equal("Connected", technics.State); // the test refreshed the live state too
    }

    [Fact]
    public async Task TestOfAMissingDeviceFails()
    {
        var mic = Item(LibraryKind.Audio, "mic");

        await mic.TestCommand.ExecuteAsync(null);

        Assert.True(mic.Failed);
        Assert.Equal("Windows couldn't find Yeti. Make sure it's plugged in or turned on, then try again.", mic.ResultMessage);
    }

    [Fact]
    public async Task TestWithoutTheEngineSaysToStartIt()
    {
        _engine.IsRunning = false;
        var technics = Item(LibraryKind.Bluetooth, "technics");

        await technics.TestCommand.ExecuteAsync(null);

        Assert.Equal("Start Daze Control to test this.", technics.ResultMessage);
    }

    [Fact]
    public async Task AndroidNotConnectedToAdbIsACautionNotAFailure()
    {
        var tv = Item(LibraryKind.Android, "living-room");

        await tv.TestCommand.ExecuteAsync(null);

        Assert.True(tv.Cautioned);
    }

    [Fact]
    public async Task AndroidThatAdbListsButCantUseIsACaution()
    {
        var answer = _engine.Answer!;
        _engine.Answer = r => r.Kind == "adb"
            ? new PipeMessage { Ok = true, Devices = [new DeviceInfo { Name = "192.168.1.50:5555", Detail = "unauthorized" }] }
            : answer(r);
        var tv = Item(LibraryKind.Android, "living-room");

        await tv.TestCommand.ExecuteAsync(null);

        Assert.True(tv.Cautioned);
        Assert.Contains("unauthorized", tv.ResultMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AProblemOfADottedKeyStaysOnItsOwnCard()
    {
        _session.Update(c => c with
        {
            Library = c.Library with { Bluetooth = [.. c.Library.Bluetooth, new BluetoothDevice("sony.spare", "nope")] },
        });

        Assert.True(Item(LibraryKind.Bluetooth, "sony.spare").HasProblem);
        Assert.False(Item(LibraryKind.Bluetooth, "sony").HasProblem);
    }

    [Fact]
    public async Task DisplayTestChecksEveryScreenOfTheLayoutIsConnected()
    {
        var desk = Item(LibraryKind.Display, "desk");
        await desk.TestCommand.ExecuteAsync(null);
        Assert.Equal("Its screen is connected.", desk.ResultMessage);

        var answer = _engine.Answer!;
        _engine.Answer = r => r.Kind == "displays" ? new PipeMessage { Ok = true, Devices = [new DeviceInfo { Name = "HDMI-1", Id = "other" }] } : answer(r);
        await desk.TestCommand.ExecuteAsync(null);
        Assert.True(desk.Failed);
        Assert.Equal("None of its screens are connected right now.", desk.ResultMessage);
    }

    [Fact]
    public async Task ProgramTestLooksForTheFileAndOnPath()
    {
        var tv = _page.Section(null).Items.Single(p => p.Title == "tv.exe");
        var notepad = _page.Section(null).Items.Single(p => p.Title == "notepad.exe");

        await tv.TestCommand.ExecuteAsync(null);
        await notepad.TestCommand.ExecuteAsync(null);

        Assert.Equal("Found the program.", tv.ResultMessage);
        Assert.Equal(@"Found it: C:\Windows\notepad.exe", notepad.ResultMessage);
        Assert.Empty(_engine.Requests);

        _files.Clear();
        await tv.TestCommand.ExecuteAsync(null);
        Assert.True(tv.Failed);
    }

    [Fact]
    public async Task ItemsKeepTheirTestResultWhenTheConfigChanges()
    {
        var technics = Item(LibraryKind.Bluetooth, "technics");
        await technics.TestCommand.ExecuteAsync(null);

        _session.Update(c => c with { Flows = [.. c.Flows.Where(f => f.Id == "a")] });

        Assert.Same(technics, Item(LibraryKind.Bluetooth, "technics"));
        Assert.True(technics.HasResult);
        Assert.Equal("Used by 1 flow", technics.UsedByText);
    }

    [Fact]
    public void LibraryProblemsShowOnTheirCard()
    {
        _session.Update(c => c with { Library = c.Library with { Bluetooth = [c.Library.Bluetooth[0] with { Address = "nope" }, c.Library.Bluetooth[1]] } });

        Assert.Contains("isn't a Bluetooth address", Item(LibraryKind.Bluetooth, "technics").Problem, StringComparison.Ordinal);
        Assert.False(Item(LibraryKind.Bluetooth, "sony").HasProblem);
    }

    [Fact]
    public void RenamingALayoutKeepsTheFlowsThatRestoreIt()
    {
        _session.Update(c => c with { Flows = [.. c.Flows, new Flow("desk", "Desk", [new StepNode("l", "display.layout") { With = new([KeyValuePair.Create("layout", (ParamValue)"desk")]) }])] });

        Assert.True(_page.Rename(Item(LibraryKind.Display, "desk"), "  Work desk "));

        Assert.Equal("Work desk", Assert.Single(_session.Current.Library.Displays).Key);
        var step = (StepNode)_session.Current.Flows.Single(f => f.Id == "desk").Nodes[0];
        Assert.Equal(new ScalarParam("Work desk"), step.With["layout"]);
        Assert.Equal("Used by 1 flow", Item(LibraryKind.Display, "Work desk").UsedByText);
        Assert.DoesNotContain(_session.Problems, p => p.FlowId == "desk");
    }

    [Theory]
    [InlineData("", "Give it a name.")]
    [InlineData("living-room", null)]
    public void RenameSaysWhyItDidntHappen(string name, string? error)
    {
        _session.Update(c => c with { Library = c.Library with { Android = [.. c.Library.Android, new AndroidDevice("den", "10.0.0.9", new([]))] } });
        var den = Item(LibraryKind.Android, "den");

        Assert.False(_page.Rename(den, name));
        Assert.Equal(error ?? "\"living-room\" is already in your Library.", den.RenameError);
        Assert.Equal(["living-room", "den"], _session.Current.Library.Android.Select(d => d.Key));
    }

    [Fact]
    public void OnlyLayoutsAndAndroidDevicesCanBeRenamed()
    {
        Assert.True(Item(LibraryKind.Display, "desk").CanRename);
        Assert.True(Item(LibraryKind.Android, "living-room").CanRename);
        Assert.False(Item(LibraryKind.Bluetooth, "technics").CanRename); // its key isn't what the card shows
        Assert.False(_page.Rename(Item(LibraryKind.Bluetooth, "technics"), "buds"));
        Assert.False(_page.Section(null).Items[0].CanRename);
    }

    [Fact]
    public async Task LayoutDetailsNameAPluggedInScreenByWhatWindowsCallsItNow()
    {
        var desk = Item(LibraryKind.Display, "desk");
        Assert.Equal("DELA1F2 (not connected)", Assert.Single(desk.Screens).Name); // no live list yet

        await _page.RefreshAsync();

        var screen = Assert.Single(Item(LibraryKind.Display, "desk").Screens);
        Assert.Equal(("DELL", "DP-1"), (screen.Name, screen.Port));
    }

    [Fact]
    public void LayoutDetailsShowTheCapturedModes()
    {
        var layout = new DisplayLayout("d", [Monitor, @"\\?\DISPLAY#GSM5B7F#x"], TwoScreens);

        var screens = LibraryPageViewModel.ScreenDetails(layout, [new DeviceInfo { Name = "HDMI-2", Id = Monitor, Detail = "on" }]);

        Assert.Equal(new ScreenDetail("DELA1F2", "HDMI-2", "2560 × 1440", "0, 0", "60 Hz", true), screens[0]);
        Assert.Equal(new ScreenDetail("GSM5B7F (not connected)", "DP", "1920 × 1080", "2560, 0", "144 Hz", false), screens[1]);
        Assert.Equal("GSM5B7F (not connected), DP, 1920 × 1080, 144 Hz, at 2560, 0", screens[1].ToString());
    }

    /// <summary>Two paths (HDMI at 60 Hz, DP at 144 Hz) and their two source modes, as CcdLayout saves them.</summary>
    private static string TwoScreens
    {
        get
        {
            var bytes = new byte[8 + 2 * DisplayLayout.PathSize + 2 * DisplayLayout.ModeSize];
            void Put(int offset, int value) => BitConverter.TryWriteBytes(bytes.AsSpan(offset), value);
            Put(0, 2);
            Put(4, 2);
            for (var i = 0; i < 2; i++)
            {
                var path = 8 + i * DisplayLayout.PathSize;
                Put(path + 12, i); // source mode index
                Put(path + 32, -1); // no target mode
                Put(path + 36, i == 0 ? 5 : 10); // HDMI, DP
                Put(path + 48, i == 0 ? 60 : 144);
                Put(path + 52, 1);
                var mode = 8 + 2 * DisplayLayout.PathSize + i * DisplayLayout.ModeSize;
                Put(mode, 1); // source
                Put(mode + 16, i == 0 ? 2560 : 1920);
                Put(mode + 20, i == 0 ? 1440 : 1080);
                Put(mode + 28, i == 0 ? 0 : 2560);
            }
            return Convert.ToBase64String(bytes);
        }
    }

    [Fact]
    public void UsedByListsTheFlowsByName()
    {
        Assert.Equal([new FlowLink("a", "Connect Technics"), new FlowLink("b", "TV")], Item(LibraryKind.Bluetooth, "technics").UsedByFlows);
        Assert.Equal([new FlowLink("a", "Connect Technics"), new FlowLink("b", "TV")], Item(null, @"C:\Tools\tv.exe").UsedByFlows);
        Assert.Equal([new FlowLink("b", "TV")], Item(null, "notepad.exe").UsedByFlows);
    }

    [Fact]
    public void PickingAUsedByFlowAsksTheShellToOpenIt()
    {
        string? opened = null;
        _page.FlowRequested += id => opened = id;

        _page.OpenFlowCommand.Execute(new FlowLink("b", "TV"));

        Assert.Equal("b", opened);
    }

    [Fact]
    public async Task OpenFileLocationShowsTheResolvedFile()
    {
        var notepad = Item(null, "notepad.exe");
        Assert.True(notepad.CanOpenLocation);
        Assert.False(Item(LibraryKind.Display, "desk").CanOpenLocation);

        await _page.OpenLocationCommand.ExecuteAsync(notepad);

        Assert.Equal([@"C:\Windows\notepad.exe"], _revealed);
    }

    [Fact]
    public async Task OpenFileLocationOfAMissingFileSaysSoInstead()
    {
        _files.Clear();
        var tv = Item(null, @"C:\Tools\tv.exe");

        await _page.OpenLocationCommand.ExecuteAsync(tv);

        Assert.Empty(_revealed);
        Assert.True(tv.Failed);
        Assert.Contains("Can't find this file", tv.ResultMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineStartingRefreshesTheLiveState()
    {
        _engine.IsRunning = false;
        _engine.SetRunning(true);
        await Task.Yield();

        Assert.Equal("Connected", Item(LibraryKind.Bluetooth, "technics").State);
    }

    // ---- Android › Inputs (L1) ----

    [Fact]
    public void AndroidInputsAreAddedMovedAndRenamedWithTheirSteps()
    {
        var tv = Item(LibraryKind.Android, "living-room");
        Assert.True(tv.HasInputs);
        Assert.True(tv.HasNoInputs);
        Assert.Equal("192.168.1.50 · No inputs yet", tv.Detail);
        Assert.Equal("input", tv.NewInputName);

        Assert.True(_page.AddInput(tv, "hdmi2"));

        tv = Item(LibraryKind.Android, "living-room");
        var hdmi2 = Assert.Single(tv.Inputs);
        Assert.Equal("192.168.1.50 · 1 input", tv.Detail);
        Assert.Equal("Used by 1 flow", hdmi2.UsedByText); // flow b already picked hdmi2
        Assert.False(hdmi2.CanRemove);
        Assert.Equal("First in the list: no moves", hdmi2.MovesText);

        _page.AddDownCommand.Execute(hdmi2);
        _page.AddUpCommand.Execute(hdmi2);
        _page.AddDownCommand.Execute(hdmi2);
        Assert.Equal(["down", "up", "down"], hdmi2.Moves.Select(m => m.Move));
        _page.RemoveMoveCommand.Execute(hdmi2.Moves[1]);
        Assert.Equal("Down, Down", hdmi2.MovesText);
        Assert.Equal(["down", "down"], _session.Current.Library.Android[0].Inputs["hdmi2"]);

        Assert.True(_page.RenameInput(hdmi2, "game"));

        Assert.Equal("game", Assert.Single(Item(LibraryKind.Android, "living-room").Inputs).Name);
        var step = (StepNode)_session.Current.Flows.Single(f => f.Id == "b").FindNode("tv")!;
        Assert.Equal("game", ((ScalarParam)step.With["input"]).Text);
    }

    [Fact]
    public void AnAndroidInputNameMustBeNewOnItsDevice()
    {
        var tv = Item(LibraryKind.Android, "living-room");
        Assert.True(_page.AddInput(tv, "hdmi1"));
        tv = Item(LibraryKind.Android, "living-room");

        Assert.False(_page.AddInput(tv, " hdmi1 "));
        Assert.Equal("living-room already has an input called \"hdmi1\".", tv.AddInputError);
        Assert.False(_page.AddInput(tv, ""));
        Assert.Equal("Give it a name.", tv.AddInputError);
        Assert.True(_page.AddInput(tv, "hdmi3"));
        Assert.Null(Item(LibraryKind.Android, "living-room").AddInputError);

        var hdmi3 = Item(LibraryKind.Android, "living-room").Inputs[1];
        Assert.False(_page.RenameInput(hdmi3, "hdmi1"));
        Assert.Equal("living-room already has an input called \"hdmi1\".", hdmi3.RenameError);
    }

    [Fact]
    public void AnUnusedAndroidInputCanBeRemoved()
    {
        _page.AddInput(Item(LibraryKind.Android, "living-room"), "hdmi1");
        _page.AddInput(Item(LibraryKind.Android, "living-room"), "hdmi2");
        var inputs = Item(LibraryKind.Android, "living-room").Inputs;
        Assert.True(inputs[0].CanRemove);
        Assert.Equal("Remove this input", inputs[0].RemoveTip);
        Assert.Equal("Used by 1 flow. Change that step first.", inputs[1].RemoveTip);

        _page.RemoveInputCommand.Execute(inputs[1]); // in use: nothing happens
        _page.RemoveInputCommand.Execute(inputs[0]);

        Assert.Equal(["hdmi2"], _session.Current.Library.Android[0].Inputs.Keys);
    }

    [Fact]
    public void TheInspectorLinkOpensTheDevicesInputs()
    {
        _page.SelectedIndex = 0;

        _page.RevealInputs("living-room");

        Assert.Equal("Android", _page.Sections[_page.SelectedIndex].Title);
        Assert.True(Item(LibraryKind.Android, "living-room").ShowInputs);
    }

    [Fact]
    public void TheInspectorHintLinkShowsTheLibraryWithTheDevicesInputsOpen()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        var requested = 0;
        shell.LibraryRequested += () => requested++;
        shell.OpenFlow("b");
        shell.Editor!.Select("tv");
        var field = shell.Inspector!.Fields.Single(f => f.Name == "input");
        Assert.True(field.HasHintLink); // living-room has no inputs yet

        shell.Inspector.OpenAndroidInputs(field);

        Assert.Equal(1, requested);
        Assert.Equal("Android", shell.Library.Sections[shell.Library.SelectedIndex].Title);
        Assert.True(shell.Library.Section(LibraryKind.Android).Items.Single(i => i.Key == "living-room").ShowInputs);
    }
}
