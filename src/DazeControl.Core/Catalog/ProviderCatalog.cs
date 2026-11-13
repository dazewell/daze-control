using DazeControl.Core.Model;

namespace DazeControl.Core.Catalog;

/// <summary>
/// Every step type the engine knows, compiled in (D-006). Glyphs from UX 5.3.
/// </summary>
public static class ProviderCatalog
{
    private const string Audio = "E767", Bluetooth = "E702", Display = "E7F4", Process = "E756", Script = "E943",
        Http = "E774", Tv = "E8B2", Service = "E912", Wait = "E823", Message = "E8BD", Flag = "E7C1", File = "E8A5", RunFlow = "E768", MenuGlyph = "E700", AskGlyph = "E897", PickGlyph = "E8FD",
        Network = "E701", Power = "E83F", Pc = "E7F8", Clock = "E787", Drive = "EDA2", TextGlyph = "E8D2", StopGlyph = "E71A";

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

    /// <summary>What a program or script gives even when it fails (D-129).</summary>
    private static readonly ValueSpec ExitCode = new("exitCode", ValueKind.Number, "Exit code") { OnFailure = true, Help = "The number it ended with; 0 usually means it worked" };


    /// <summary>The checks that read a fact and say it in a sentence (D-081, D-118): each gives <c>result</c> (D-129).</summary>
    private static readonly HashSet<string> ResultSteps = new(StringComparer.Ordinal)
    {
        "audio.check", "bluetooth.check", "process.check", "service.check", "file.check", "file.space", "display.count",
        "android.check", "display.check", "flag.check",
        "network.wifi", "network.cable", "network.internet", "network.metered", "network.vpn", "network.reach",
        "power.source", "power.battery", "pc.idle", "pc.locked", "time.check", "value.check",
    };

    private static readonly ValueSpec Result = new("result", ValueKind.Text, "Result")
    {
        OnFailure = true, Help = "What the check found, in a sentence (\"The battery is at 20%\")",
    };

    /// <summary>What a program or script printed: the last 1,000 characters, read only when a later step uses it.</summary>
    private static readonly ValueSpec Output = new("output", ValueKind.Text, "Output")
    {
        Sensitive = true, Help = "What it printed (the last 1,000 characters). Only read when a later step uses it",
    };

    public static ValueList<ProviderDescriptor> All { get; } = [.. Declared().Select(d => ResultSteps.Contains(d.Id) ? d with { Values = [.. d.Values, Result] } : d)];

    private static ValueList<ProviderDescriptor> Declared() =>
    [
        new("audio.setDefault", "Set default device", Audio, Short,
        [
            Ref("device", "Audio device", LibraryKind.Audio),
            new("roles", ParamKind.Choice, "Use for") { Choices = ["all", "console", "communications"], Captions = ["Everything", "Media and system sounds", "Calls only"], Default = "all" },
        ])
        {
            Resources = ["audio"],
            Conditions = [new ConditionSpec("notDefault", "It isn't the default", "audio.isDefault", false)],
            Values =
            [
                new ValueSpec("device", ValueKind.Device, "Device") { Library = LibraryKind.Audio, Help = "The Library name of the device it made the default" },
                // D-130: a later Set default device can put it back. Only a device in the Library can be given.
                new ValueSpec("previous", ValueKind.Device, "Previous default") { Library = LibraryKind.Audio, Help = "The device that was the default before, if it's in your Library" },
            ],
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
        new("audio.media", "Control media", Audio, Short,
        [
            new("action", ParamKind.Choice, "Action")
            {
                Choices = ["playPause", "play", "pause", "stop", "next", "previous"],
                Captions = ["Play or pause", "Play", "Pause", "Stop", "Next track", "Previous track"],
                Default = "playPause", Help = "Goes to the app that is playing on this PC. It can't tell whether anything played.",
            },
        ])
        { Keywords = "play pause next previous track music video media key" },
        new("bluetooth.connect", "Connect", Bluetooth, TimeSpan.FromSeconds(20),
            [Ref("device", "Bluetooth device", LibraryKind.Bluetooth)])
        {
            Resources = ["bluetooth:{device}"],
            Conditions = [new ConditionSpec("disconnected", "It isn't connected", BluetoothConnected, false)],
            Values = [new ValueSpec("device", ValueKind.Device, "Device") { Library = LibraryKind.Bluetooth, Help = "The Library name of the device it connected" }],
        },
        new("bluetooth.disconnect", "Disconnect", Bluetooth, TimeSpan.FromSeconds(20),
            [Ref("device", "Bluetooth device", LibraryKind.Bluetooth)])
        {
            Resources = ["bluetooth:{device}"],
            Conditions = [new ConditionSpec("connected", "It's connected", BluetoothConnected, true)],
            Values = [new ValueSpec("device", ValueKind.Device, "Device") { Library = LibraryKind.Bluetooth, Help = "The Library name of the device it disconnected" }],
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
        new("display.count", "Check screen count", Display, Short,
        [
            new("count", ParamKind.WholeNumber, "Screens") { Default = "2", Min = 1, Max = 16 },
            new("is", ParamKind.Choice, "Is") { Choices = ["atLeast", "fewer"], Captions = ["At least this many are on", "Fewer are on"], Default = "atLeast", Help = WaitUntilHelp },
        ])
        {
            Keywords = "monitor external second screens how many",
            Values = [new ValueSpec("count", ValueKind.Number, "Screens on") { OnFailure = true, Help = "How many screens are on" }],
        },
        new("process.run", "Run program", Process, TimeSpan.FromSeconds(30),
        [
            new("path", ParamKind.FilePath, "Program") { Required = true },
            new("args", ParamKind.TextList, "Arguments") { TakesValues = true, Fill = FillRule.Argument, Help = "One argument per line. A value from an earlier step stays one argument, whatever it holds." },
            new("cwd", ParamKind.FolderPath, "Start in"),
            new("wait", ParamKind.Boolean, "Wait until it exits") { Default = "true" },
            new("exitCodes", ParamKind.TextList, "Exit codes that mean success") { Default = "0" },
        ])
        {
            Conditions = [new ConditionSpec("notRunning", "It isn't running", "process.running", false)],
            Values = [ExitCode, Output],
        },
        new("process.open", "Open file or address", Process, TimeSpan.FromSeconds(30),
        [
            new("target", ParamKind.Text, "File, folder or address")
            {
                Required = true, TakesValues = true, Fill = FillRule.Target,
                Help = "Opens in the app that handles it, without waiting. A value from an earlier step stays part of the name or address, never a command.",
                Examples =
                [
                    new("A web page", "https://example.com"),
                    new("A Steam game", "steam://rungameid/730"),
                    new("Windows settings", "ms-settings:display"),
                    new("A folder", @"%USERPROFILE%\Downloads"),
                ],
            },
        ])
        { Keywords = "launch website url link document folder default app steam" },
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
        new(PowerShellId, "Run PowerShell", Script, TimeSpan.FromSeconds(60),
            [new("script", ParamKind.MultilineText, "Script")
            {
                Required = true, Examples = PowerShellExamples,
                Help = "Values from earlier steps are not pasted into the script: read them as $env:DAZE_<STEP>_<VALUE>, like $env:DAZE_BT_DEVICE.",
            }])
        {
            Values = [ExitCode, Output],
        },
        new("http.request", "Send web request", Http, Short,
        [
            new("url", ParamKind.Text, "Address")
            {
                Required = true, Examples = [new("Sunshine: close the running app", "https://localhost:47990/api/apps/close")],
                TakesValues = true, Fill = FillRule.Url,
            },
            new("method", ParamKind.Choice, "Method") { Choices = ["GET", "POST", "PUT", "PATCH", "DELETE"], Default = "GET" },
            new("headers", ParamKind.TextMap, "Headers") { Examples = [new("JSON", "Content-Type: application/json")] },
            new("body", ParamKind.MultilineText, "Body") { Examples = [new("JSON", "{ \"key\": \"value\" }")] },
            new("credential", ParamKind.Text, "Sign in with")
            {
                Help = "Name of a saved credential; the secret stays in Windows Credential Manager.",
                Suggest = ParamSuggest.Devices("credentials"),
            },
        ])
        {
            Values =
            [
                new ValueSpec("status", ValueKind.Number, "Status code") { OnFailure = true, Help = "The number the server answered with (200 = fine)" },
                new ValueSpec("body", ValueKind.Text, "Response") { Sensitive = true, Help = "What the server sent back (the first 1,000 characters). Only read when a later step uses it" },
            ],
        },
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
                Required = true, Help = "Opens in the app that handles it.", TakesValues = true, Fill = FillRule.Url,
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
        new("file.space", "Check free space", Drive, Short,
        [
            new("drive", ParamKind.Text, "Drive or folder") { Default = "C:", Help = "Like C: or D:\\Backups; %VARIABLES% work." },
            new("gb", ParamKind.WholeNumber, "Free space (GB)") { Default = "10", Min = 1, Max = 1_000_000 },
            new("is", ParamKind.Choice, "Is") { Choices = ["atLeast", "below"], Captions = ["At least this much is free", "Less is free"], Default = "atLeast", Help = WaitUntilHelp },
        ])
        {
            Keywords = "disk drive storage full",
            Values = [new ValueSpec("free", ValueKind.Number, "Free space (GB)") { OnFailure = true, Help = "How many GB are free" }],
        },
        new("network.wake", "Wake over network", Network, TimeSpan.FromSeconds(15),
        [
            new("mac", ParamKind.Text, "Hardware (MAC) address")
            {
                Required = true, TakesValues = true,
                Help = "Of the PC or device to wake, like aa:bb:cc:dd:ee:ff (dashes or aabb.ccdd.eeff work too). Sent on every network this PC is connected to. It must be set up to wake on the network.",
                Examples = [new("With colons", "aa:bb:cc:dd:ee:ff"), new("With dashes", "aa-bb-cc-dd-ee-ff")],
            },
        ]) { Keywords = "wol wake on lan magic packet power on turn on nas server" },
        // Network, power, PC and time checks (D-118): read-only, one read when the step runs.
        new("network.wifi", "Check Wi-Fi", Network, Short,
        [
            new("network", ParamKind.Text, "Network") { Suggest = ParamSuggest.Devices("wifi"), Help = "The Wi-Fi network's name. Empty = any Wi-Fi network." },
            new("is", ParamKind.Choice, "Is") { Choices = ["connected", "notConnected"], Captions = ["Connected", "Not connected"], Default = "connected", Help = WaitUntilHelp },
        ])
        {
            Keywords = "wifi wlan ssid wireless home office",
            Values = [new ValueSpec("network", ValueKind.Text, "Wi-Fi network") { OnFailure = true, Help = "The name of the Wi-Fi network it is connected to" }],
        },
        new("network.cable", "Check network cable", Network, Short,
        [
            new("is", ParamKind.Choice, "Is") { Choices = ["connected", "notConnected"], Captions = ["Connected", "Not connected"], Default = "connected", Help = WaitUntilHelp },
        ]) { Keywords = "ethernet lan wired dock" },
        new("network.internet", "Check internet", Network, Short,
        [
            new("is", ParamKind.Choice, "Is")
            {
                Choices = ["online", "offline"], Captions = ["Reachable", "Not reachable"], Default = "online",
                Help = "Uses Windows' own internet check; nothing is sent. " + WaitUntilHelp,
            },
        ]) { Keywords = "online offline connected web" },
        new("network.metered", "Check metered connection", Network, Short,
        [
            new("is", ParamKind.Choice, "Is")
            {
                Choices = ["metered", "notMetered"], Captions = ["Metered", "Not metered"], Default = "metered",
                Help = "Metered as set in Windows, or roaming, or over the data limit. " + WaitUntilHelp,
            },
        ]) { Keywords = "data limit hotspot mobile roaming" },
        new("network.vpn", "Check VPN", Network, Short,
        [
            new("name", ParamKind.Text, "VPN") { Help = "Part of the VPN adapter's name, like WireGuard. Empty = any VPN." },
            new("is", ParamKind.Choice, "Is") { Choices = ["connected", "notConnected"], Captions = ["Connected", "Not connected"], Default = "connected", Help = WaitUntilHelp },
        ]) { Keywords = "tunnel work remote" },
        // T24.8 (D-140): one probe, only when the step runs: a TCP connect, a web request or a ping, then closed.
        new("network.reach", "Check address", Network, TimeSpan.FromSeconds(30),
        [
            new("address", ParamKind.Text, "Address")
            {
                Required = true, TakesValues = true,
                Help = "host:port connects to that port, a web address (http:// or https://) asks for the page, a host name alone pings it. Only this one probe is sent.",
                Examples = [new("A port", "nas:445"), new("A web page", "https://example.com"), new("A ping", "192.168.1.1")],
            },
            new("is", ParamKind.Choice, "Is") { Choices = ["reachable", "notReachable"], Captions = ["Reachable", "Not reachable"], Default = "reachable", Help = WaitUntilHelp },
            new("status", ParamKind.Text, "Web answers that count")
            {
                Help = "For a web address: like 200-299 or 200, 401. Empty = any answer counts.",
                Examples = [new("Any good answer", "200-299")],
            },
            new("method", ParamKind.Choice, "Ask for") { Choices = ["GET", "HEAD"], Captions = ["The page (GET)", "Only its headers (HEAD)"], Default = "GET", Help = "For a web address. Nothing of the page is kept." },
            new("timeout", ParamKind.Duration, "Give up after") { Default = "5s", Help = "1 to 20 s for an answer." },
        ])
        {
            Keywords = "ping reachable online up server nas port tcp connect website host answer",
            Values =
            [
                new ValueSpec("ms", ValueKind.Number, "Answer time (ms)") { OnFailure = true, Help = "How long the answer took, in milliseconds" },
                new ValueSpec("status", ValueKind.Number, "Status code") { OnFailure = true, Help = "The number a web address answered with (200 = fine)" },
            ],
        },
        new("power.source", "Check power", Power, Short,
        [
            new("is", ParamKind.Choice, "Is") { Choices = ["pluggedIn", "onBattery"], Captions = ["Plugged in", "On battery"], Default = "pluggedIn", Help = WaitUntilHelp },
        ]) { Keywords = "ac charger plugged battery laptop" },
        new("power.battery", "Check battery", Power, Short,
        [
            new("percent", ParamKind.WholeNumber, "Charge (%)") { Default = "20", Min = 1, Max = 100 },
            new("is", ParamKind.Choice, "Is") { Choices = ["atLeast", "below"], Captions = ["At least this much", "Below this"], Default = "atLeast", Help = WaitUntilHelp },
        ])
        {
            Keywords = "charge level low laptop",
            Values = [new ValueSpec("percent", ValueKind.Number, "Battery (%)") { OnFailure = true, Help = "The charge it read" }],
        },
        new("pc.idle", "Check idle time", Pc, Short,
        [
            new("for", ParamKind.Duration, "No input for") { Default = "5m", Help = "Time since the last key press or mouse move." },
            new("is", ParamKind.Choice, "Is") { Choices = ["idle", "active"], Captions = ["Idle at least this long", "Used more recently"], Default = "idle", Help = WaitUntilHelp },
        ])
        {
            Keywords = "away inactive afk keyboard mouse",
            Values = [new ValueSpec("idle", ValueKind.Text, "Idle time") { OnFailure = true, Help = "How long since the last key press or mouse move, like 1h 5m" }],
        },
        new("pc.locked", "Check locked", Pc, Short,
        [
            new("is", ParamKind.Choice, "Is") { Choices = ["locked", "unlocked"], Captions = ["Locked", "Unlocked"], Default = "locked", Help = WaitUntilHelp },
        ]) { Keywords = "lock screen signed away" },
        new("time.check", "Check time", Clock, Short,
        [
            new("days", ParamKind.Choice, "Days") { Choices = ["daily", "weekdays", "weekends"], Captions = ["Every day", "Monday to Friday", "Saturday and Sunday"], Default = "daily" },
            new("from", ParamKind.Text, "From") { Help = "Like 07:00. Empty, with To empty = all day." },
            new("to", ParamKind.Text, "To") { Help = "Like 22:30. Earlier than From = past midnight." },
            new("is", ParamKind.Choice, "Is") { Choices = ["inside", "outside"], Captions = ["Now is in this time", "Now is outside it"], Default = "inside", Help = "Fails when it isn't. To wait until it is, set Try again and Wait between tries." },
        ]) { Keywords = "time of day weekday weekend night morning hours schedule" },
        new("wait", "Wait", Wait, TimeSpan.FromHours(1),
            [new("duration", ParamKind.Duration, "How long") { Required = true }]),
        new("notify", "Show message", Message, Short,
        [
            new("text", ParamKind.Text, "Message") { Required = true, TakesValues = true },
            new("sound", ParamKind.Boolean, "Play the notification sound") { Default = "true" },
        ]),
        // T23.7 (DESIGN §9): one composed text, set once per run, that several later steps use as {id.text}. Not a variable.
        new(MakeTextId, "Make text", TextGlyph, Short,
        [
            new(StepValues.MakeTextParam, ParamKind.MultilineText, "Text")
            {
                Required = true, TakesValues = true,
                Help = "Put values from earlier steps together once; later steps use the result as one value.",
                Examples = [new("A device and the time", "{bt.device} at {@time}")],
            },
        ])
        {
            Keywords = "compose join combine value string",
            Values = [new ValueSpec(StepValues.MakeTextParam, ValueKind.Text, "Text") { Help = "The text it made" }],
        },
        // T24.3 (D-137): compares two texts that take values; fails when it doesn't hold, so the failure link is the "else".
        new(ValueCheckId, "Check value", TextGlyph, Short,
        [
            new("value", ParamKind.Text, "Value")
            {
                TakesValues = true,
                Help = "What to check, usually a value from an earlier step. Spaces at the ends don't count.",
                Examples = [new("An answer", "{ask.answer}"), new("A web status", "{web.status}")],
            },
            new("is", ParamKind.Choice, "Is")
            {
                Choices = ["is", "isNot", "contains", "doesNotContain", "startsWith", "endsWith", "isEmpty", "isNotEmpty", "atLeast", "below"],
                Captions = ["Is", "Isn't", "Contains", "Doesn't contain", "Starts with", "Ends with", "Is empty", "Isn't empty", "Is at least (numbers)", "Is below (numbers)"],
                Default = "is",
                Help = "Fails when it isn't, so a link for \"if it fails\" is the else. Numbers compare as numbers (5 is 5.0); at least and below need numbers.",
            },
            new("to", ParamKind.Text, "To") { TakesValues = true, Help = "What to compare with. Not used by Is empty and Isn't empty." },
            new("ignoreCase", ParamKind.Boolean, "Ignore upper and lower case") { Default = "true" },
        ])
        { Keywords = "if compare equal condition branch text number answer status contains" },
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
            new("title", ParamKind.Text, "Title") { TakesValues = true, Help = "Empty = this flow's name." },
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
        ])
        {
            Values = [new ValueSpec("picked", ValueKind.Text, "Picked flow") { Help = "The name of the flow that was picked" }],
        },
        // No resources: the prompt waits for a person, never for a device (D-107).
        new(PromptAskId, "Ask", AskGlyph, TimeSpan.FromMinutes(11),
        [
            new("mode", ParamKind.Choice, "Kind")
            {
                Choices = [Prompts.Confirm, Prompts.Choose], Captions = ["Yes or No", "Choose"], Default = Prompts.Confirm,
                Help = "Yes or No: Yes goes on, No fails the step (a \"when it fails\" branch can follow). Choose: pick one of 2 to 12 answers.",
            },
            new("question", ParamKind.Text, "Question") { Required = true, TakesValues = true },
            new("choices", ParamKind.TextList, "Answers")
            {
                Ordered = true, Help = "Choose only: 2 to 12 answers, in order. Keys 1 to 9 pick the first nine.",
            },
            new("flag", ParamKind.Text, "Remember the answer in flag")
            {
                Suggest = ParamSuggest.Config(ParamSuggest.Flags),
                Help = "Yes turns the flag on, No off. For Choose, the flag \"name:answer\" turns on and the others off, so \"Check flag\" can branch on it.",
            },
            new("timeout", ParamKind.Duration, "Wait")
            {
                Default = "30s", Help = "5 s to 10 min for an answer.",
            },
            new("onTimeout", ParamKind.Choice, "No answer")
            {
                Choices = [Prompts.Stop, Prompts.UseDefault, Prompts.Skip], Captions = ["Stop the flow", "Use the default answer", "Carry on without it"],
                Help = "Empty = stop the flow (its history says \"No answer\"), so a run nobody is there for never waits.",
            },
            new("default", ParamKind.Text, "Default answer")
            {
                Help = "Used when nobody answers and \"No answer\" says so: yes or no, or one of the answers.",
            },
        ])
        {
            Values = [new ValueSpec("answer", ValueKind.Text, "Answer") { Help = "Yes or No, or the answer chosen" }],
        },
        // A person picks a Library item; later steps use it as "$result:<id>" (D-117). No resources, like Ask.
        new(PromptPickId, "Pick device", PickGlyph, TimeSpan.FromMinutes(11),
        [
            new("kind", ParamKind.Choice, "Pick")
            {
                Choices = [.. PickResults.KindWords], Captions = ["Audio device", "Bluetooth device", "Screen layout", "Android device", "Bound device"],
                Default = "audio", Help = "Lists your Library's items of this kind. Later steps can use the one picked.",
            },
            new("question", ParamKind.Text, "Question") { TakesValues = true, Help = "Empty = \"Pick an audio device\" (or the kind you chose)." },
            new("timeout", ParamKind.Duration, "Wait")
            {
                Default = "30s", Help = "5 s to 10 min for an answer.",
            },
            new("onTimeout", ParamKind.Choice, "No answer")
            {
                Choices = [Prompts.Stop, Prompts.UseDefault], Captions = ["Stop the flow", "Use the default"],
                Help = "Empty = stop the flow (its history says \"No answer\"), so a run nobody is there for never waits.",
            },
            new("default", ParamKind.Text, "Default")
            {
                LibraryFrom = "kind", Help = "The Library item to use when nobody answers, and the one shown first.",
            },
        ])
        {
            Values = [new ValueSpec("device", ValueKind.Device, "Device") { Help = "The Library name of the one picked" }],
        },
        // T24.1, D-133: no resources, nothing to wait for; the guard after a check.
        new(StopFlowId, "Stop flow", StopGlyph, Short,
        [
            new("mode", ParamKind.Choice, "Stop")
            {
                Choices = ["quiet", "failed"], Captions = ["Quietly", "As failed"], Default = "quiet",
                Help = "Quietly: nothing is shown and it isn't a problem. As failed: the flow counts as not finished, with your message.",
            },
            new("message", ParamKind.Text, "Message")
            {
                TakesValues = true, Help = "Shown in the run's history, and as the reason when it stops as failed.",
                Examples = [new("Say why", "Not at home")],
            },
        ])
        {
            Keywords = "guard end exit quit cancel abort halt return only if",
        },
    ];

    /// <summary>The <c>flow.stop</c> step type (T24.1, D-133).</summary>
    public const string StopFlowId = "flow.stop";

    /// <summary>The <c>prompt.pick</c> step type (D-117).</summary>
    public const string PromptPickId = "prompt.pick";

    /// <summary>The <c>prompt.ask</c> step type (M20, D-107).</summary>
    public const string PromptAskId = "prompt.ask";

    /// <summary>The <c>menu.show</c> step type (M12, D-054).</summary>
    public const string MenuShowId = "menu.show";

    /// <summary>The <c>script.powershell</c> step type: gets earlier values as environment variables (D-129).</summary>
    public const string PowerShellId = "script.powershell";

    /// <summary><c>menu.show</c>'s item count and <c>closeAfter</c> range (DESIGN §2.1).</summary>
    public const int MaxMenuItems = 12;

    public static readonly TimeSpan MinCloseAfter = TimeSpan.FromSeconds(5), MaxCloseAfter = TimeSpan.FromMinutes(10);

    /// <summary>The <c>flow.run</c> step type (D-061, D-062).</summary>
    public const string RunFlowId = "flow.run";

    /// <summary>Make text (T23.7): gives its filled <c>text</c> setting as the value <c>text</c>.</summary>
    public const string MakeTextId = "text.make";

    public const string ValueCheckId = "value.check";

    /// <summary>Check address (T24.8).</summary>
    public const string ReachId = "network.reach";

    /// <summary>
    /// The read-only check steps (D-081, D-118): they change nothing and fail when the fact isn't what was chosen.
    /// <c>dctl check</c> runs only these.
    /// </summary>
    public static IReadOnlySet<string> Checks { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "audio.check", "bluetooth.check", "display.check", "display.count", "process.check", "android.check", "service.check",
        "file.check", "file.space", "flag.check", "network.wifi", "network.cable", "network.internet", "network.metered",
        "network.vpn", "network.reach", "power.source", "power.battery", "pc.idle", "pc.locked", "time.check", "value.check",
    };

    public static ProviderDescriptor? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    private static ParamSpec Ref(string name, string title, LibraryKind kind, bool required = true, string? help = null) =>
        new(name, ParamKind.LibraryRef, title) { Library = kind, Required = required, Help = help };
}
