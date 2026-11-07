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
            Ref("device", "Device", LibraryKind.Audio),
            new("roles", ParamKind.Choice, "Use for") { Choices = ["all", "console", "communications"], Default = "all" },
        ])
        {
            Resources = ["audio"],
            Conditions = [new ConditionSpec("notDefault", "It isn't the default", "audio.isDefault", false)],
        },
        new("audio.check", "Check default device", Audio, Short,
        [
            Ref("device", "Device", LibraryKind.Audio),
            new("is", ParamKind.Choice, "Is") { Choices = ["default", "notDefault"], Default = "default", Help = WaitUntilHelp },
        ]),
        new("audio.setVolume", "Set volume", Audio, Short,
        [
            Ref("device", "Device", LibraryKind.Audio, required: false, help: "Empty = the current default device."),
            new("percent", ParamKind.WholeNumber, "Volume (%)") { Required = true, Min = 0, Max = 100 },
        ])
        { Resources = ["audio"] },
        new("audio.mute", "Mute", Audio, Short,
        [
            Ref("device", "Device", LibraryKind.Audio, required: false, help: "Empty = the current default device."),
            new("muted", ParamKind.Boolean, "Muted") { Default = "true" },
        ])
        { Resources = ["audio"] },
        new("bluetooth.connect", "Connect", Bluetooth, TimeSpan.FromSeconds(20),
            [Ref("device", "Device", LibraryKind.Bluetooth)])
        {
            Resources = ["bluetooth:{device}"],
            Conditions = [new ConditionSpec("disconnected", "It isn't connected", BluetoothConnected, false)],
        },
        new("bluetooth.disconnect", "Disconnect", Bluetooth, TimeSpan.FromSeconds(20),
            [Ref("device", "Device", LibraryKind.Bluetooth)])
        {
            Resources = ["bluetooth:{device}"],
            Conditions = [new ConditionSpec("connected", "It is connected", BluetoothConnected, true)],
        },
        // Read-only: takes the device's lock so it answers after a Connect that is still running.
        new("bluetooth.check", "Check connection", Bluetooth, Short,
        [
            Ref("device", "Device", LibraryKind.Bluetooth),
            new("is", ParamKind.Choice, "Is") { Choices = ["connected", "disconnected"], Default = "connected", Help = WaitUntilHelp },
        ])
        { Resources = ["bluetooth:{device}"] },
        new("display.topology", "Project", Display, Short,
        [
            new("mode", ParamKind.Choice, "Mode") { Required = true, Choices = ["internal", "clone", "extend", "external"] },
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
        new("process.check", "Check a program is running", Process, Short,
        [
            new("program", ParamKind.Text, "Program") { Required = true, Help = "Its name or path, like Spotify or Spotify.exe." },
            new("is", ParamKind.Choice, "Is") { Choices = ["running", "notRunning"], Default = "running", Help = WaitUntilHelp },
        ]),
        new("script.powershell", "Run PowerShell", Script, TimeSpan.FromSeconds(60),
            [new("script", ParamKind.MultilineText, "Script") { Required = true, Examples = PowerShellExamples }]),
        new("http.request", "Web request", Http, Short,
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
        new("android.input", "Android input", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("input", ParamKind.Text, "Input")
            {
                Required = true, Help = "One of the inputs saved with the Android device.",
                Suggest = ParamSuggest.Config(ParamSuggest.AndroidInputs),
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.keys", "Android keys", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("keys", ParamKind.TextList, "Keys")
            {
                Required = true, Help = "Sent in order. Alt+Up or Alt+Down on a key's × moves it.",
                Suggest = ParamSuggest.Static("androidKeys", AndroidKeys.Names), Ordered = true,
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.sleep", "Android sleep", Tv, TimeSpan.FromSeconds(20),
            [Ref("device", "Android device", LibraryKind.Android)])
        { Resources = ["adb:{device}"] },
        new("android.start", "Start Android app", Tv, TimeSpan.FromSeconds(20),
        [
            Ref("device", "Android device", LibraryKind.Android),
            new("app", ParamKind.Text, "App")
            {
                Required = true, Help = "Pick one from the list, or type its package name (like com.spotify.music).",
                Suggest = ParamSuggest.Devices(ParamSuggest.AndroidApps),
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.connect", "Connect Android device", Tv, TimeSpan.FromSeconds(20),
            [Ref("device", "Android device", LibraryKind.Android)])
        { Resources = ["adb:{device}"] },
        new("service.start", "Start service", Service, TimeSpan.FromSeconds(30),
            [new("name", ParamKind.Text, "Service name") { Required = true, Suggest = ParamSuggest.Devices("services") }])
        {
            Resources = ["service:{name}"],
            Conditions = [new ConditionSpec("stopped", "It isn't running", "service.running", false)],
        },
        new("service.stop", "Stop service", Service, TimeSpan.FromSeconds(30),
            [new("name", ParamKind.Text, "Service name") { Required = true, Suggest = ParamSuggest.Devices("services") }])
        {
            Resources = ["service:{name}"],
            Conditions = [new ConditionSpec("running", "It's running", "service.running", true)],
        },
        new("service.check", "Check a service", Service, Short,
        [
            new("name", ParamKind.Text, "Service name") { Required = true, Suggest = ParamSuggest.Devices("services") },
            new("is", ParamKind.Choice, "Is") { Choices = ["running", "stopped"], Default = "running", Help = WaitUntilHelp },
        ]),
        new("file.check", "Check a file or folder", File, Short,
        [
            new("path", ParamKind.Text, "File or folder") { Required = true, Help = "A full path; %VARIABLES% work." },
            new("is", ParamKind.Choice, "Is") { Choices = ["exists", "missing"], Default = "exists", Help = WaitUntilHelp },
        ]),
        new("wait", "Wait", Wait, TimeSpan.FromHours(1),
            [new("duration", ParamKind.Duration, "How long") { Required = true }]),
        new("notify", "Show a message", Message, Short,
            [new("text", ParamKind.Text, "Message") { Required = true }]),
        new("flag.set", "Set a flag", Flag, Short,
        [
            new("name", ParamKind.Text, "Flag") { Required = true, Suggest = ParamSuggest.Config(ParamSuggest.Flags) },
            new("value", ParamKind.Boolean, "On") { Default = "true" },
        ]),
        new("flag.check", "Check a flag", Flag, Short,
        [
            new("name", ParamKind.Text, "Flag") { Required = true, Suggest = ParamSuggest.Config(ParamSuggest.Flags) },
            new("value", ParamKind.Boolean, "Is on") { Default = "true", Help = WaitUntilHelp },
        ]),
        // No resources: the step holds no lock while it waits; the called flow's steps take their own (D-062).
        new(RunFlowId, "Run a flow", RunFlow, TimeSpan.FromMinutes(10),
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
        new(MenuShowId, "Show a menu", MenuGlyph, TimeSpan.FromMinutes(10),
        [
            new("items", ParamKind.TextList, "Flows")
            {
                Required = true, Ordered = true, Suggest = ParamSuggest.Config(ParamSuggest.Flows), RefersTo = ConfigRef.Flow,
                Help = "1 to 12 flows, in menu order. Keys 1 to 9 pick the first nine.",
            },
            new("title", ParamKind.Text, "Title") { Help = "Empty = this flow's name." },
            new("layout", ParamKind.Choice, "Layout") { Choices = ["list", "grid"], Default = "list" },
            new("size", ParamKind.Choice, "Size")
            {
                Choices = ["normal", "large"], Default = "normal", Help = "Large is for a TV across the room.",
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
