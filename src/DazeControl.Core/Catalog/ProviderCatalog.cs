using DazeControl.Core.Model;

namespace DazeControl.Core.Catalog;

/// <summary>
/// Every step type the engine knows, compiled in (D-006). Glyphs from UX 5.3.
/// </summary>
public static class ProviderCatalog
{
    private const string Audio = "E767", Bluetooth = "E702", Display = "E7F4", Process = "E756", Script = "E943",
        Http = "E774", Tv = "E8B2", Service = "E912", Wait = "E823", Message = "E8BD", Flag = "E7C1";

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    /// <summary>UX §5.4.</summary>
    private static readonly ValueList<ParamExample> PowerShellExamples =
    [
        new("Open a website", "Start-Process 'https://example.com'"),
        new("Start an app and wait", "Start-Process notepad -Wait"),
        new("Close an app", "Stop-Process -Name 'Spotify' -ErrorAction SilentlyContinue"),
        new("Lock the PC", "rundll32.exe user32.dll,LockWorkStation"),
        new("Fail unless an app is running", "if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { exit 1 }"),
    ];

    public static ValueList<ProviderDescriptor> All { get; } =
    [
        new("audio.setDefault", "Set default device", Audio, Short,
        [
            Ref("device", "Device", LibraryKind.Audio),
            new("roles", ParamKind.Choice, "Use for") { Choices = ["all", "console", "communications"], Default = "all" },
        ])
        { Resources = ["audio"] },
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
        { Resources = ["bluetooth:{device}"] },
        new("bluetooth.disconnect", "Disconnect", Bluetooth, TimeSpan.FromSeconds(20),
            [Ref("device", "Device", LibraryKind.Bluetooth)])
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
                Required = true, Help = "Monitor names as Windows shows them.", Suggest = ParamSuggest.Devices("displays"),
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
                Required = true, Help = "Android key names, sent in order: home, back, dpad_down, media_play_pause.",
                Suggest = ParamSuggest.Static("androidKeys", AndroidKeys.Names), Ordered = true,
            },
        ])
        { Resources = ["adb:{device}"] },
        new("android.sleep", "Android sleep", Tv, TimeSpan.FromSeconds(20),
            [Ref("device", "Android device", LibraryKind.Android)])
        { Resources = ["adb:{device}"] },
        new("service.start", "Start service", Service, TimeSpan.FromSeconds(30),
            [new("name", ParamKind.Text, "Service name") { Required = true, Suggest = ParamSuggest.Devices("services") }])
        { Resources = ["service:{name}"] },
        new("service.stop", "Stop service", Service, TimeSpan.FromSeconds(30),
            [new("name", ParamKind.Text, "Service name") { Required = true, Suggest = ParamSuggest.Devices("services") }])
        { Resources = ["service:{name}"] },
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
            new("value", ParamKind.Boolean, "Is on") { Default = "true", Help = "Fails when the flag differs; follow it with an \"if it fails\" branch." },
        ]),
    ];

    public static ProviderDescriptor? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    private static ParamSpec Ref(string name, string title, LibraryKind kind, bool required = true, string? help = null) =>
        new(name, ParamKind.LibraryRef, title) { Library = kind, Required = required, Help = help };
}
