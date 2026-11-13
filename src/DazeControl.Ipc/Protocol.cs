using System.Security.Principal;
using System.Text.Json.Serialization;
using DazeControl.Core.Runs;

namespace DazeControl.Ipc;

/// <summary>
/// Named-pipe protocol (D-011): one JSON object per line each way. The client sends <see cref="PipeRequest"/>s; the
/// engine answers each with a <see cref="PipeMessage"/> of the same <see cref="PipeMessage.Id"/>, and after
/// <see cref="Ops.Subscribe"/> also sends events (<see cref="PipeMessage.Event"/>, id 0). A property missing from
/// the JSON reads as null/0/false whatever its initializer (source-gen, OSD X-003): the "" defaults below are only
/// for values the engine always writes.
/// </summary>
public static class Protocol
{
    public const int Version = 1;

    /// <summary>
    /// What a request that carries <see cref="PipeRequest.Inputs"/> says (T24.9). An engine that doesn't know the field would
    /// ignore it and run the flow on its defaults, so such a request is stamped 2 and an older engine refuses it by version
    /// ("speaks protocol 1, not 2"); every other request stays 1 and works against any engine.
    /// </summary>
    public const int WithInputs = 2;

    /// <summary>
    /// What <see cref="Ops.Flag"/> and <see cref="Ops.Pause"/> say (T27.8). An older engine would answer "unknown operation" at best
    /// and, worse, could read a bare flag/pause as a status request; stamped 3, it refuses by version.
    /// </summary>
    public const int WithControl = 3;

    /// <summary>
    /// What a <see cref="Ops.Run"/> with <see cref="PipeRequest.Node"/> says ("Run from this step", T27.4). An older engine would
    /// ignore the field and run the whole flow; stamped 4, it refuses by version.
    /// </summary>
    public const int WithStartStep = 4;

    /// <summary>The newest version this build speaks; the engine accepts 1 up to this.</summary>
    public const int Latest = WithStartStep;

    /// <summary>What <see cref="PipeServer"/> says when the version is not one it speaks.</summary>
    public const string MismatchStart = "The engine speaks protocol ";

    /// <summary>The version a client stamps on <paramref name="request"/>.</summary>
    public static int VersionFor(PipeRequest request) =>
        request.Op is Ops.Run && !string.IsNullOrEmpty(request.Node) ? WithStartStep
        : request.Op is Ops.Flag or Ops.Pause ? WithControl : request.Inputs is { Count: > 0 } ? WithInputs : Version;

    /// <summary>Pipe name for a user; the SID keeps sessions of different users apart.</summary>
    public static string PipeName(string userSid) => $"DazeControl-{userSid}";

    /// <summary>The current user's pipe.</summary>
    public static string CurrentPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return PipeName(identity.User!.Value);
    }
}

public static class Ops
{
    public const string Run = "run";
    public const string Cancel = "cancel";
    public const string List = "list";
    public const string Status = "status";
    public const string History = "history";
    public const string Validate = "validate";
    public const string Subscribe = "subscribe";
    public const string Autostart = "autostart";
    public const string Devices = "devices";
    public const string TestStep = "test-step";
    public const string CaptureDisplayLayout = "capture-display-layout";
    public const string AndroidApps = "android-apps";
    public const string Shortcuts = "shortcuts";
    public const string CreateShortcut = "create-shortcut";
    public const string RemoveShortcut = "remove-shortcut";

    /// <summary>flag: <see cref="PipeRequest.Key"/> is the flag's name; <see cref="PipeRequest.Enable"/> sets it, null just tells (T27.8).</summary>
    public const string Flag = "flag";

    /// <summary>pause: <see cref="PipeRequest.Enable"/> true pauses shortcuts, false resumes, null just tells, like the tray item (T27.8).</summary>
    public const string Pause = "pause";

    /// <summary>The editor's shortcut box has focus (<see cref="PipeRequest.On"/>) or lost it: the engine lets go of
    /// its shortcuts meanwhile, so the box can record a chord the engine owns (D-072). Ends when the connection does.</summary>
    public const string RecordHotkey = "record-hotkey";
}

public sealed class PipeRequest
{
    public int Protocol { get; init; }

    public int Id { get; init; }

    public string? Op { get; init; }

    /// <summary>run, cancel, history: flow id or name.</summary>
    public string? Flow { get; init; }

    /// <summary>run: answer when the run has finished, with its record.</summary>
    public bool Wait { get; init; }

    /// <summary>run, create-shortcut, remove-shortcut: <see cref="Flow"/> is an id, never a name (a desktop shortcut or
    /// the editor, D-049): a deleted flow's id must not reach another flow called like that id.</summary>
    public bool ExactId { get; init; }

    /// <summary>run (desktop shortcut) and the shortcut ops (editor): the full path of the config meant. The engine and
    /// its pipe are per user, not per config: an engine running another config refuses instead of using its own flows.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>run: walk the flow with steps that do nothing (no history, no locks).</summary>
    public bool DryRun { get; init; }

    /// <summary>run: the flow's inputs given from outside (T24.9), name → Library key (device inputs) or text. Data only:
    /// checked against the flow's inputs by the engine, never read as a command or a reference.</summary>
    public Dictionary<string, string>? Inputs { get; init; }

    /// <summary>autostart, flag, pause: true = on, false = off, null = just tell.</summary>
    public bool? Enable { get; init; }

    /// <summary>record-hotkey: true = the box is recording, false = it stopped.</summary>
    public bool? On { get; init; }

    /// <summary>history: at most this many, newest first.</summary>
    public int? Limit { get; init; }

    /// <summary>run: for the history ("Command line", "Editor").</summary>
    public string? Trigger { get; init; }

    /// <summary>devices: audio, bluetooth, displays, adb or gamepads.</summary>
    public string? Kind { get; init; }

    /// <summary>capture-display-layout: the library key the layout is saved under; flag: the flag's name.</summary>
    public string? Key { get; init; }

    /// <summary>android-apps: the device's address ("host" or "host:port", as in the library).</summary>
    public string? Address { get; init; }

    /// <summary>android-apps: the device's own adb.exe path, if the library names one.</summary>
    public string? Adb { get; init; }

    /// <summary>android-apps: true = ask the device now (connects), false = answer from the saved list (D-093).</summary>
    public bool Refresh { get; init; }

    /// <summary>test-step: the step's id in <see cref="Flow"/>. run (protocol 4, T27.4): the step to start at; the steps before it
    /// and on other branches count as done without running (D-149). Null or empty = the whole flow.</summary>
    public string? Node { get; init; }

    /// <summary>test-step: config.yaml text holding the flow and the library as the editor shows them, so a test
    /// never waits for a save and a reload.</summary>
    public string? Config { get; init; }
}

public sealed class PipeMessage
{
    public int Id { get; init; }

    public bool Ok { get; init; }

    /// <summary>Plain words for the user when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    public FlowInfo[]? Flows { get; init; }

    public RunRecord? Run { get; init; }

    public RunRecord[]? History { get; init; }

    public EngineStatus? Status { get; init; }

    public ProblemInfo[]? Problems { get; init; }

    public AutostartInfo? Autostart { get; init; }

    public PipeEvent? Event { get; init; }

    public DeviceInfo[]? Devices { get; init; }

    /// <summary>capture-display-layout: a <c>library:</c> snippet to paste into config.yaml.</summary>
    public string? Yaml { get; init; }

    /// <summary>shortcuts: all of ours on the desktop; create-shortcut: the one written; remove-shortcut: those removed.</summary>
    public ShortcutInfo[]? Shortcuts { get; init; }

    /// <summary>With <see cref="Ok"/> false: the request itself was wrong (an input the flow doesn't have), so a script
    /// gets the usage exit code instead of "the run failed".</summary>
    public bool Usage { get; init; }

    /// <summary>flag: the flag is on now; pause: shortcuts are paused now.</summary>
    public bool? State { get; init; }

    public static PipeMessage Fail(int id, string error) => new() { Id = id, Error = error };

    public static PipeMessage BadUsage(int id, string error) => new() { Id = id, Error = error, Usage = true };
}

public sealed class FlowInfo
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>A valid version exists and the flow is on.</summary>
    public bool Runnable { get; init; }

    /// <summary>The file's version has errors; the last valid one runs (D-021).</summary>
    public bool Stale { get; init; }

    public bool Enabled { get; init; }

    public bool Running { get; init; }

    public string[]? Hotkeys { get; init; }

    public int Errors { get; init; }

    /// <summary>"Only run if" (T25.1) held back its latest start, after its latest run: "Held back: Wi-Fi isn't Home." Null = not.</summary>
    public string? HeldBack { get; init; }
}

public sealed class EngineStatus
{
    public string Version { get; init; } = "";

    public int ProcessId { get; init; }

    public string ConfigPath { get; init; } = "";

    public bool ShortcutsPaused { get; init; }

    public string[]? Running { get; init; }

    /// <summary>"Alt+Z: Another app uses Alt+Z. Try another one."</summary>
    public string[]? ShortcutProblems { get; init; }

    public int Errors { get; init; }

    public int Warnings { get; init; }
}

public sealed class ProblemInfo
{
    public string Severity { get; init; } = "";

    public string Path { get; init; } = "";

    public string Message { get; init; } = "";

    public string? FlowId { get; init; }
}

public sealed class AutostartInfo
{
    /// <summary>"off", "on" or "disabledByWindows".</summary>
    public string State { get; init; } = "";

    public string? Path { get; init; }
}

/// <summary>One device for <c>dctl devices</c> and the editor's pickers.</summary>
public sealed class DeviceInfo
{
    /// <summary>What a step's <c>with:</c> takes: an endpoint name, a Bluetooth address, a port.</summary>
    public string Name { get; init; } = "";

    /// <summary>"Output, default", "DELL S2722QC, on"; Bluetooth: the device's name in Windows.</summary>
    public string? Detail { get; init; }

    /// <summary>Bluetooth: "connected" or "not connected" (null when Windows doesn't say); screens "on" or "off";
    /// services "running", "stopped", "paused" or "changing".</summary>
    public string? State { get; init; }

    /// <summary>Bluetooth: paired, but without audio, so Daze Control can't connect it (D-052).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NotConnectable { get; init; }

    public string? Id { get; init; }

    /// <summary>What a suggestion picker writes into a step (D-051): a service's name, a screen's monitor name.</summary>
    public string? Value { get; init; }
}

/// <summary>A desktop shortcut that runs a flow (D-049).</summary>
public sealed class ShortcutInfo
{
    public string FlowId { get; init; } = "";

    /// <summary>What the desktop shows (the file name without ".lnk").</summary>
    public string Name { get; init; } = "";

    public string Path { get; init; } = "";

    /// <summary>The flow is in the config.</summary>
    public bool FlowExists { get; init; }

    /// <summary>The shortcut is called what the flow is called now; false → the editor offers "Update".</summary>
    public bool CurrentName { get; init; }
}

public static class EventKinds
{
    public const string RunStarted = "runStarted";
    public const string NodeStarted = "nodeStarted";
    public const string NodeFinished = "nodeFinished";
    public const string RunFinished = "runFinished";
    public const string ConfigReloaded = "configReloaded";

    /// <summary>Which shortcuts failed changed (a retry got a freed chord, D-072): read <c>status</c> again.</summary>
    public const string ShortcutsChanged = "shortcutsChanged";
}

public sealed class PipeEvent
{
    public string Kind { get; init; } = "";

    public DateTimeOffset At { get; init; }

    public Guid? RunId { get; init; }

    public string? FlowId { get; init; }

    public int? NodeCount { get; init; }

    public string? NodeId { get; init; }

    public int? Attempt { get; init; }

    public NodeResult? Node { get; init; }

    public FlowOutcome? Outcome { get; init; }

    /// <summary>A nested run's events (a <c>flow.run</c> step that waits, D-062): the calling run and step. Additive:
    /// protocol stays 1.</summary>
    public Guid? ParentRunId { get; init; }

    public string? ParentNodeId { get; init; }

    public static PipeEvent From(RunEvent e)
    {
        var pipe = Map(e);
        return e.ParentRunId is null ? pipe : new()
        {
            Kind = pipe.Kind, At = pipe.At, RunId = pipe.RunId, FlowId = pipe.FlowId, NodeCount = pipe.NodeCount,
            NodeId = pipe.NodeId, Attempt = pipe.Attempt, Node = pipe.Node, Outcome = pipe.Outcome,
            ParentRunId = e.ParentRunId, ParentNodeId = e.ParentNodeId,
        };
    }

    private static PipeEvent Map(RunEvent e) => e switch
    {
        RunStarted s => new() { Kind = EventKinds.RunStarted, At = s.At, RunId = s.RunId, FlowId = s.FlowId, NodeCount = s.NodeCount },
        NodeStarted s => new() { Kind = EventKinds.NodeStarted, At = s.At, RunId = s.RunId, FlowId = s.FlowId, NodeId = s.NodeId, Attempt = s.Attempt },
        NodeFinished f => new() { Kind = EventKinds.NodeFinished, At = f.At, RunId = f.RunId, FlowId = f.FlowId, NodeId = f.Result.NodeId, Node = f.Result },
        RunFinished f => new() { Kind = EventKinds.RunFinished, At = f.At, RunId = f.RunId, FlowId = f.FlowId, Outcome = f.Outcome },
        _ => new() { Kind = e.GetType().Name, At = e.At, RunId = e.RunId, FlowId = e.FlowId },
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeMessage))]
public sealed partial class IpcJson : JsonSerializerContext;
