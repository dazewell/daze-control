using DazeControl.Core.Model;

namespace DazeControl.Core.Catalog;

/// <summary>
/// Every step type the engine knows, compiled in (D-006). Glyphs from UX 5.3.
/// </summary>
public static class ProviderCatalog
{
    private const string Audio = "E767", Bluetooth = "E702", Display = "E7F4", Process = "E756", Script = "E943",
        Http = "E774", Tv = "E8B2", Service = "E912", Wait = "E823", Message = "E8BD", Flag = "E7C1", File = "E8A5", RunFlow = "E768", MenuGlyph = "E700";

    /// <summary>The fact id of <c>BluetoothConnectedProbe</c> (D-081).</summary>
    public const string BluetoothConnected = "bluetooth.connected";

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    /// <summary>The "Wait until" recipe in the check steps' help (T18.5, D-081).</summary>
    private const string WaitUntilHelp = "Fails when it isn't. To wait until it is, set Try again and Wait between tries.";

    /// <summary>UX §5.4.</summary>
    private static readonly ValueList<ParamExample> PowerShellExamples =
    [
        new("Open a website", "Start-Process 'https://example.com'"),
        new("Start an app and wait", "Start-Process notepad -Wait"),
        new("Close an app", "Stop-Process -Name 'Spotify' -ErrorAction SilentlyContinue"),
        new("Lock the PC", "rundll32.exe user32.dll,LockWorkStation"),
        new("Put the PC to sleep", "rundll32.exe powrprof.dll,SetSuspendState 0,1,0"),
        new("Switch to the Balanced power plan", "powercfg /setactive SCHEME_BALANCED"),
        new("Turn on dark mode", "Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize' AppsUseLightTheme 0"),
        new("Fail unless an app is running", "if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { exit 1 }"),
    ];

    public static ValueList<ProviderDescriptor> All { get; } =
    [
        new("audio.setDefault", "Set default device", Audio, Short,
        [
            Ref("device", "Audio device", LibraryKind.Audio),
            new("roles", ParamKind.Choice, "Use for") { Choices = ["all", "console", "communications"], Captions = ["Everything", "Media and system sounds", "Calls only"], Default = "all" },
        ])
        {
            Resources = ["audio"],
            Conditions = [new ConditionSpec("notDefault", "It isn't the default", "audio.isDefault", false)],
        },
        new("audio.check", "Check default device", Audio, Short,
        [
            Ref("device", "Audio device", LibraryKind.Audio),
            new("is", ParamKind.Choice, "Is") { Choices = ["default", "notDefault"], Captions = ["It is the default", "It isn't the default"], Default = "default", Help = WaitUntilHelp },
        ]),
        new("audio.setVolume", "Set volume", Audio, Short,
        [
            Ref("device", "Audio device", LibraryKind.Audio, required: false, help: "Empty = the current default device."),
            new("percent", ParamKind.WholeNumber, "Volume (%)") { Required = true, Min = 0, Max = 100 },
        ])
        { Resources = ["audio"] },
        new("audio.mute", "Mute or unmute", Audio, Short,
        [
            Ref("device", "Audio device", LibraryKind.Audio, required: false, help: "Empty = the current default device."),
            new("muted", ParamKind.Boolean, "Muted") { Default = "true" },
        ])
        { Resources = ["audio"] },
        new("bluetooth.connect", "Connect", Bluetooth, TimeSpan.FromSeconds(20),
            [Ref("device", "Bluetooth device", LibraryKind.Bluetooth)])
        {
            Resources = ["bluetooth:{device}"],
            Conditions = [new ConditionSpec("disconnected", "It isn't connected", BluetoothConnected, false)],
        },
        new("bluetooth.disconnect", "Disconnect", Bluetooth, TimeSpan.FromSeconds(20),
            [Ref("device", "Bluetooth device", LibraryKind.Bluetooth)])
        {
            Resources = ["bluetooth:{device}"],
            Conditions = [new ConditionSpec("connected", "It's connected", BluetoothConnected, true)],
        },
        // Read-only: takes the device's lock so it answers after a Connect that is still running.
        new("bluetooth.check", "Check connection", Bluetooth, Short,
        [
            Ref("device", "Bluetooth device", LibraryKind.Bluetooth),
            new("is", ParamKind.Choice, "Is") { Choices = ["connected", "disconnected"], Captions = ["It's connected", "It isn't connected"], Default = "connected", Help = WaitUntilHelp },
        ])
        { Resources = ["bluetooth:{device}"] },
        new("display.topology", "Project screens", Display, Short,
        [
            new("mode", ParamKind.Choice, "Mode") { Required = true, Choices = ["internal", "clone", "extend", "external"], Captions = ["This PC only", "Duplicate", "Extend", "Second screen only"] },
        ])
        { Resources = ["display"] },
        new("display.outputs", "Use only these screens", Display, Short,
        [
            new("outputs", ParamKind.TextList, "Screens")
            {
                Required = true, Help = "The others are turned off.", Suggest = ParamSuggest.Devices("displays"),
            },
        ])
        { Resources = ["display"] },
        new("display.layout", "Restore screen layout", Display, Short,
            [Ref("layout", "Layout", LibraryKind.Display, help: "A layout captured with dctl capture-display-layout.")])
        { Resources = ["display"] },
        new("display.screensOff", "Turn screens off", Display, Short, [])
        {
            Resources = ["display"],
            Keywords = "monitor power sleep blank",
        },
        // Read-only (D-077): compares the screens on now with a captured layout; changes nothing.
        new("display.check", "Check layout", Display, Short,
        [
            Ref("layout", "Layout", LibraryKind.Display, help: "A layout captured with dctl capture-display-layout."),
            new("is", ParamKind.Choice, "Is") { Choices = ["matches", "differs"], Captions = ["The screens match it", "The screens differ from it"], Default = "matches", Help = WaitUntilHelp },
        ])
        { Resources = ["display"] },
        new("process.run", "Run program", Process, TimeSpan.FromSeconds(30),
        [
            new("path", ParamKind.FilePath, "Program") { Required = true },
            new("args", ParamKind.TextList, "Arguments"),
            new("cwd", ParamKind.FolderPath, "Start in"),
            new("wait", ParamKind.Boolean, "Wait until it exits") { Default = "true" },
            new("exitCodes", ParamKind.TextList, "Exit codes that mean success") { Default = "0" },
        ])
        {
            Conditions = [new ConditionSpec("notRunning", "It isn't running", "process.running", false)],
        },
        new("process.close", "Close program", Process, TimeSpan.FromSeconds(30),
        [
            new("program", ParamKind.Text, "Program") { Required = true, Help = "Its name or path, like Spotify or Spotify.exe. Not running counts as done." },
            new("force", ParamKind.Duration, "End it after")
            {
                Default = "10s", Help = "It is asked to close first; if it is still there after this long, it is ended (with the programs it started). The step itself gives up after 30 s.",
            },
        ]),
        new("process.check", "Check program", Process, Short,
        [
            new("program", ParamKind.Text, "Program") { Required = true, Help = "Its name or path, like Spotify or Spotify.exe." },
            new("is", ParamKind.Choice, "Is") { Choices = ["running", "notRunning"], Captions = ["Running", "Not running"], Default = "running", Help = WaitUntilHelp },
        ]) { Keywords = "running is" },
        new("script.powershell", "Run PowerShell", Script, TimeSpan.FromSeconds(60),
            [new("script", ParamKind.MultilineText, "Script") { Required = true, Examples = PowerShellExamples }]),
        new("http.request", "Send web request", Http, Short,
        [
            new("url", ParamKind.Text, "Address")
            {
                Required = true, Examples = [new("Sunshine: close the running app", "https://localhost:47990/api/apps/close")],
            },
            new("method", ParamKind.Choice, "Method") { Choices = ["GET", "POST", "PUT", "PATCH", "DELETE"], Default = "GET" },
            new("headers", ParamKind.TextMap, "Headers") { Examples = [new("JSON", "Content-Type: application/json")] },
            new("body", ParamKind.MultilineText, "Body") { Examples = [new("JSON", "{ \"key\": \"value\" }")] },
            new("credential", ParamKind.Text, "Sign in with")
            {
                Help = "Name of a saved credential; the secret stays in Windows Credential Manager.",
                Suggest = ParamSuggest.Devices("credentials"),
            },
        ]),
        // Android order (REVIEW F6): connect, apps, input and keys, media and sound, screen, wake/sleep.
        new("android.connect", "Connect", Tv, TimeSpan.FromSeconds(20),
            [Ref("device", "Android device", LibraryKind.Android)])
        { Resources = ["adb:{device}"] },
        new("android.start", "Start app", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("app", ParamKind.Text, "App")
            {
                Required = true, Help = "Pick one from the list, or type its package name (like com.spotify.music).",
                Suggest = ParamSuggest.Devices(ParamSuggest.AndroidApps),
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.stop", "Stop app", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("app", ParamKind.Text, "App")
            {
                Required = true, Help = "Pick one from the list, or type its package name (like com.spotify.music). Not running counts as done.",
                Suggest = ParamSuggest.Devices(ParamSuggest.AndroidApps),
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.open", "Open address", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("address", ParamKind.Text, "Address")
            {
                Required = true, Help = "Opens in the app that handles it.",
                Examples = [new("A web page", "https://example.com"), new("A YouTube video", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")],
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.input", "Choose input", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("input", ParamKind.Text, "Input")
            {
                Required = true, Help = "One of the inputs saved with the Android device.",
                Suggest = ParamSuggest.Config(ParamSuggest.AndroidInputs),
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.keys", "Send keys", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("keys", ParamKind.TextList, "Keys")
            {
                Required = true, Help = "Sent in order. Alt+Up or Alt+Down on a key's × moves it.",
                Suggest = ParamSuggest.Static("androidKeys", AndroidKeys.Names), Ordered = true,
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.media", "Control media", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("action", ParamKind.Choice, "Action")
            {
                Choices = ["playPause", "play", "pause", "stop", "next", "previous", "fastForward", "rewind"],
                Captions = ["Play or pause", "Play", "Pause", "Stop", "Next track", "Previous track", "Fast forward", "Rewind"],
                Default = "playPause", Help = "Goes to the app that is playing.",
            },
        ])
        { Resources = ["adb:{device}"], Keywords = "play pause next previous track music video" },
        new("android.setVolume", "Set volume", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("stream", ParamKind.Choice, "Volume of")
            {
                Choices = ["media", "ring", "alarm", "notification", "system"],
                Captions = ["Media", "Ringtone", "Alarms", "Notifications", "System sounds"], Default = "media",
            },
            new("percent", ParamKind.WholeNumber, "Volume (%)") { Required = true, Min = 0, Max = 100, Help = "A share of the device's own range." },
        ])
        { Resources = ["adb:{device}"] },
        new("android.changeVolume", "Change volume", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("direction", ParamKind.Choice, "Direction") { Choices = ["up", "down"], Captions = ["Louder", "Quieter"], Default = "up" },
            new("times", ParamKind.WholeNumber, "Steps") { Min = 1, Max = 30, Default = "1", Help = "How many times the volume key is pressed." },
        ])
        { Resources = ["adb:{device}"] },
        new("android.mute", "Mute or unmute", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("muted", ParamKind.Boolean, "Muted") { Default = "true" },
        ])
        { Resources = ["adb:{device}"] },
        new("android.setBrightness", "Set brightness", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("percent", ParamKind.WholeNumber, "Brightness (%)") { Required = true, Min = 0, Max = 100, Help = "Turns automatic brightness off." },
        ])
        { Resources = ["adb:{device}"] },
        new("android.wake", "Wake", Tv, TimeSpan.FromSeconds(20),
            [Ref("device", "Android device", LibraryKind.Android)])
        { Resources = ["adb:{device}"] },
        new("android.sleep", "Sleep", Tv, TimeSpan.FromSeconds(20),
            [Ref("device", "Android device", LibraryKind.Android)])
        { Resources = ["adb:{device}"] },
        // Read-only, and only asks an adb that is already running: it never starts one (D-077).
        new("android.check", "Check connection", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("is", ParamKind.Choice, "Is") { Choices = ["connected", "disconnected"], Captions = ["It's connected", "It isn't connected"], Default = "connected", Help = WaitUntilHelp },
        ])
        { Resources = ["adb:{device}"] },
        new("service.start", "Start service", Service, TimeSpan.FromSeconds(30),
            [new("name", ParamKind.Text, "Service") { Required = true, Suggest = ParamSuggest.Devices("services") }])
        {
            Resources = ["service:{name}"],
            Conditions = [new ConditionSpec("stopped", "It isn't running", "service.running", false)],
        },
        new("service.stop", "Stop service", Service, TimeSpan.FromSeconds(30),
            [new("name", ParamKind.Text, "Service") { Required = true, Suggest = ParamSuggest.Devices("services") }])
        {
            Resources = ["service:{name}"],
            Conditions = [new ConditionSpec("running", "It's running", "service.running", true)],
        },
        new("service.check", "Check service", Service, Short,
        [
            new("name", ParamKind.Text, "Service") { Required = true, Suggest = ParamSuggest.Devices("services") },
            new("is", ParamKind.Choice, "Is") { Choices = ["running", "stopped"], Captions = ["Running", "Stopped"], Default = "running", Help = WaitUntilHelp },
        ]),
        new("file.check", "Check file", File, Short,
        [
            new("path", ParamKind.Text, "File or folder") { Required = true, Help = "A full path; %VARIABLES% work." },
            new("is", ParamKind.Choice, "Is") { Choices = ["exists", "missing"], Captions = ["It exists", "It's missing"], Default = "exists", Help = WaitUntilHelp },
        ]) { Keywords = "folder exists" },
        new("wait", "Wait", Wait, TimeSpan.FromHours(1),
            [new("duration", ParamKind.Duration, "How long") { Required = true }]),
        new("notify", "Show message", Message, Short,
            [new("text", ParamKind.Text, "Message") { Required = true }]),
        new("flag.set", "Set flag", Flag, Short,
        [
            new("name", ParamKind.Text, "Flag") { Required = true, Suggest = ParamSuggest.Config(ParamSuggest.Flags) },
            new("value", ParamKind.Boolean, "On") { Default = "true" },
        ]),
        new("flag.check", "Check flag", Flag, Short,
        [
            new("name", ParamKind.Text, "Flag") { Required = true, Suggest = ParamSuggest.Config(ParamSuggest.Flags) },
            new("value", ParamKind.Boolean, "Is on") { Default = "true", Help = WaitUntilHelp },
        ]),
        // No resources: the step holds no lock while it waits; the called flow's steps take their own (D-062).
        new(RunFlowId, "Run flow", RunFlow, TimeSpan.FromMinutes(10),
        [
            new("flow", ParamKind.Text, "Flow")
            {
                Required = true, Suggest = ParamSuggest.Config(ParamSuggest.Flows), RefersTo = ConfigRef.Flow,
            },
            new("wait", ParamKind.Boolean, "Wait for it to finish")
            {
                Default = "true",
                Help = "On: steps after this wait for it, and its result decides what runs next. Off: it starts on its own, and this flow carries on at once.",
            },
            // Input name → a Library key of that input's kind, or this flow's own $name (D-064); missing = its default.
            new(FlowInputs.Param, ParamKind.TextMap, "Inputs")
            {
                Help = "Which device each of its inputs gets. Inputs not given use that flow's defaults.",
                Examples = [new("Another device", "output: sony")],
            },
        ]),
        // No resources yet: T12.4 decides how menus serialize (the presenter's gate, DESIGN §3.2).
        new(MenuShowId, "Show menu", MenuGlyph, TimeSpan.FromMinutes(10),
        [
            new("items", ParamKind.TextList, "Flows")
            {
                Required = true, Ordered = true, Suggest = ParamSuggest.Config(ParamSuggest.Flows), RefersTo = ConfigRef.Flow,
                Help = "1 to 12 flows, in menu order. Keys 1 to 9 pick the first nine.",
            },
            new("title", ParamKind.Text, "Title") { Help = "Empty = this flow's name." },
            new("layout", ParamKind.Choice, "Layout") { Choices = ["list", "grid"], Captions = ["List", "Grid"], Default = "list" },
            new("size", ParamKind.Choice, "Size")
            {
                Choices = ["normal", "large"], Captions = ["Normal", "Large"], Default = "normal", Help = "Large is for a TV across the room.",
            },
            new("closeAfter", ParamKind.Duration, "Close after")
            {
                Default = "30s", Help = "5 s to 10 min with no input; any input starts the count again.",
            },
            new("wait", ParamKind.Boolean, "Wait for it to finish")
            {
                Default = "false",
                Help = "On: this step waits for the picked flow and takes its result. Off: the picked flow starts on its own.",
            },
        ]),
    ];

    /// <summary>The <c>menu.show</c> step type (M12, D-054).</summary>
    public const string MenuShowId = "menu.show";

    /// <summary><c>menu.show</c>'s item count and <c>closeAfter</c> range (DESIGN §2.1).</summary>
    public const int MaxMenuItems = 12;

    public static readonly TimeSpan MinCloseAfter = TimeSpan.FromSeconds(5), MaxCloseAfter = TimeSpan.FromMinutes(10);

    /// <summary>The <c>flow.run</c> step type (D-061, D-062).</summary>
    public const string RunFlowId = "flow.run";

    public static ProviderDescriptor? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    private static ParamSpec Ref(string name, string title, LibraryKind kind, bool required = true, string? help = null) =>
        new(name, ParamKind.LibraryRef, title) { Library = kind, Required = required, Help = help };
}
