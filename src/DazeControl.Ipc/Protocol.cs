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

    /// <summary>run: walk the flow with steps that do nothing (no history, no locks).</summary>
    public bool DryRun { get; init; }

    /// <summary>autostart: true = on, false = off, null = just tell.</summary>
    public bool? Enable { get; init; }

    /// <summary>history: at most this many, newest first.</summary>
    public int? Limit { get; init; }

    /// <summary>run: for the history ("Command line", "Editor").</summary>
    public string? Trigger { get; init; }
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

    public static PipeMessage Fail(int id, string error) => new() { Id = id, Error = error };
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

public static class EventKinds
{
    public const string RunStarted = "runStarted";
    public const string NodeStarted = "nodeStarted";
    public const string NodeFinished = "nodeFinished";
    public const string RunFinished = "runFinished";
    public const string ConfigReloaded = "configReloaded";
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

    public static PipeEvent From(RunEvent e) => e switch
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
