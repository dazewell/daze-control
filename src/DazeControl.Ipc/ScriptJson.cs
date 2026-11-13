using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DazeControl.Core.Runs;

namespace DazeControl.Ipc;

/// <summary>
/// <c>dctl --json</c> (T27.8, D-142): one compact JSON document per command, written by hand with
/// <see cref="Utf8JsonWriter"/> (no reflection serializer, Native AOT). The shape is a contract for scripts: a field is
/// added, never renamed or removed, and a document only ever holds what the human output prints. Nothing here reads a step
/// value, an input, a header or a credential (D-014): flows, status, run summaries, device names, a check's own sentence.
/// </summary>
public static class ScriptJson
{
    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string>? items)
    {
        w.WriteStartArray(name);
        foreach (var item in items ?? [])
            w.WriteStringValue(item);
        w.WriteEndArray();
    }

    /// <summary>The flow's state word: <c>off</c>, <c>needsFixing</c>, <c>lastGoodVersion</c>, <c>running</c> or <c>ready</c>.</summary>
    public static string StateOf(FlowInfo flow) =>
        !flow.Enabled ? "off" : !flow.Runnable ? "needsFixing" : flow.Stale ? "lastGoodVersion" : flow.Running ? "running" : "ready";

    /// <summary><c>list</c>. <paramref name="engineRunning"/> false = read from config.yaml (running, stale and errors are then unknown: false / 0).</summary>
    public static string Flows(IEnumerable<FlowInfo> flows, bool engineRunning) => Write(w =>
    {
        w.WriteBoolean("engineRunning", engineRunning);
        w.WriteStartArray("flows");
        foreach (var f in flows)
        {
            w.WriteStartObject();
            w.WriteString("id", f.Id);
            w.WriteString("name", f.Name);
            w.WriteString("state", StateOf(f));
            w.WriteBoolean("enabled", f.Enabled);
            w.WriteBoolean("runnable", f.Runnable);
            w.WriteBoolean("stale", f.Stale);
            w.WriteBoolean("running", f.Running);
            Strings(w, "hotkeys", f.Hotkeys);
            w.WriteNumber("errors", f.Errors);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary><c>status</c>.</summary>
    public static string Status(EngineStatus s) => Write(w =>
    {
        w.WriteString("version", s.Version);
        w.WriteNumber("processId", s.ProcessId);
        w.WriteString("configPath", s.ConfigPath);
        w.WriteNumber("errors", s.Errors);
        w.WriteNumber("warnings", s.Warnings);
        Strings(w, "running", s.Running);
        w.WriteBoolean("shortcutsPaused", s.ShortcutsPaused);
        Strings(w, "shortcutProblems", s.ShortcutProblems);
    });

    /// <summary><c>history</c>, newest first: a summary per run, never a step's values or messages.</summary>
    public static string History(IEnumerable<RunRecord> runs) => Write(w =>
    {
        w.WriteStartArray("runs");
        foreach (var r in runs)
        {
            w.WriteStartObject();
            w.WriteString("runId", r.RunId);
            w.WriteString("flowId", r.FlowId);
            w.WriteString("flowName", r.FlowName);
            w.WriteString("trigger", r.Trigger);
            w.WriteString("started", r.Started.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteNumber("durationSeconds", Math.Round(r.Duration.TotalSeconds, 3));
            w.WriteString("outcome", Outcome(r.Outcome));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary><c>devices &lt;kind&gt;</c>: what a step's setting can name.</summary>
    public static string Devices(string? kind, IEnumerable<DeviceInfo> devices) => Write(w =>
    {
        w.WriteString("kind", kind);
        w.WriteStartArray("devices");
        foreach (var d in devices)
        {
            w.WriteStartObject();
            w.WriteString("name", d.Name);
            w.WriteString("detail", d.Detail);
            w.WriteString("state", d.State);
            w.WriteBoolean("notConnectable", d.NotConnectable);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary><c>check</c>: <paramref name="ok"/> is the exit code (0 yes, 1 no); <paramref name="message"/> the check's own sentence.</summary>
    public static string Check(string check, bool ok, string message) => Write(w =>
    {
        w.WriteString("check", check);
        w.WriteBoolean("ok", ok);
        w.WriteString("message", message);
    });

    /// <summary><c>flag &lt;name&gt;</c>.</summary>
    public static string Flag(string name, bool on) => Write(w =>
    {
        w.WriteString("flag", name);
        w.WriteBoolean("on", on);
    });

    /// <summary><c>pause</c>.</summary>
    public static string Pause(bool paused) => Write(w => w.WriteBoolean("paused", paused));

    private static string Outcome(FlowOutcome outcome) => outcome switch
    {
        FlowOutcome.Succeeded => "succeeded",
        FlowOutcome.PartiallySucceeded => "partiallySucceeded",
        FlowOutcome.Cancelled => "cancelled",
        _ => "failed",
    };
}
