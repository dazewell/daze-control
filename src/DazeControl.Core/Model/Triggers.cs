using System.Globalization;

namespace DazeControl.Core.Model;

/// <summary>Weekdays a schedule runs on (M20, D-107). Written as daily / weekdays / weekends or a list of mon..sun.</summary>
[Flags]
public enum Days : byte
{
    None = 0,
    Mon = 1,
    Tue = 2,
    Wed = 4,
    Thu = 8,
    Fri = 16,
    Sat = 32,
    Sun = 64,
    Weekdays = Mon | Tue | Wed | Thu | Fri,
    Weekends = Sat | Sun,
    Daily = Weekdays | Weekends,
}

/// <summary>What a schedule does when its time passed while the PC slept or the engine was closed (DESIGN §2).</summary>
public enum IfMissed
{
    Run,
    Skip,
}

/// <summary>
/// One way a flow starts by itself (M20, DESIGN §1). Schedules (<see cref="ScheduleTrigger"/>) are computed by
/// <see cref="ScheduleTable"/>; <see cref="EventTrigger"/> and <see cref="EngineStartTrigger"/> are fired by the runtime.
/// <see cref="Only"/> is a step condition id (M14) checked when the trigger fires; the runtime validates it (T20.3).
/// </summary>
public abstract record Trigger
{
    public string? Only { get; init; }
}

/// <summary>A trigger with due times. <see cref="Grace"/> null = the setting's default (10 min).</summary>
public abstract record ScheduleTrigger : Trigger
{
    public IfMissed IfMissed { get; init; } = IfMissed.Run;

    public TimeSpan? Grace { get; init; }
}

/// <summary><c>at: "22:30"</c> on <see cref="Days"/>, local time.</summary>
public sealed record AtTrigger(TimeOnly Time, Days Days) : ScheduleTrigger;

/// <summary>
/// <c>every: 2h</c>, counted from the start of the window (<c>between</c>, ends included; an end before the start
/// crosses midnight) or, without one, from local midnight, restarting each day.
/// </summary>
public sealed record EveryTrigger(TimeSpan Interval, TimeOnly? From = null, TimeOnly? To = null) : ScheduleTrigger
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    /// <summary>Unwindowed intervals restart at each midnight, so a longer one would never come due.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(24);
}

/// <summary><c>once: "2026-12-01 09:30"</c>, local time.</summary>
public sealed record OnceTrigger(DateTime When) : ScheduleTrigger;

/// <summary><c>on: engine.start</c>: when the engine starts (at logon).</summary>
public sealed record EngineStartTrigger : Trigger
{
    public const string Name = "engine.start";
}

/// <summary><c>on: session.unlock</c> and friends (<see cref="TriggerText.Events"/>).</summary>
public sealed record EventTrigger(string Name) : Trigger
{
    /// <summary><c>ssid: Home</c> on a network event: only that Wi-Fi network (null = any network). Implies Wi-Fi.</summary>
    public string? Ssid { get; init; }

    /// <summary><c>via: wifi|cable</c> on a network event: only connections of that kind (null = any connection).</summary>
    public NetworkVia? Via { get; init; }

    /// <summary>
    /// <c>device:</c> (T24.6): on <c>device.added</c>/<c>device.removed</c> only a device whose name contains this text (any case);
    /// on <c>audio.default-changed</c> only when the new default is this Library audio device (its key). Null = any device.
    /// </summary>
    public string? Device { get; init; }

    /// <summary>
    /// <c>flow:</c> (T26.3) on <c>flow.finished</c>/<c>flow.failed</c>: only when that flow (its id, trimmed like every flow
    /// reference) ended. Null = any flow. Renamed with the flow (<see cref="FlowRefs.Rename"/>, D-075).
    /// </summary>
    public string? Flow { get; init; }

    /// <summary>
    /// <c>path:</c> (T26.5) on <c>file.created</c>/<c>file.changed</c>/<c>file.deleted</c>: the folder to watch, as written
    /// (environment variables stay unexpanded; <see cref="TriggerText.ResolveFolder"/> makes the full path).
    /// </summary>
    public string? Path { get; init; }

    /// <summary><c>filter:</c> (T26.5) on a file event: a file name pattern with <c>*</c> and <c>?</c> (like <c>*.pdf</c>). Null = any name.</summary>
    public string? Filter { get; init; }
}

/// <summary>The kind of connection a network event is about (<c>via:</c>); each connection raises its own event.</summary>
public enum NetworkVia
{
    WiFi,
    Cable,
}

/// <summary>Text forms of triggers, shared by the YAML reader, the validator and (later) the editor.</summary>
public static class TriggerText
{
    /// <summary>The v1 event names (DESIGN §3).</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "session.lock", "session.unlock", "session.logon", "session.logoff",
        "power.resume", "power.ac", "power.battery",
        "device.added", "device.removed",
        "audio.default-changed", "display.changed",
        "network.connected", "network.disconnected",
        "display.off", "display.on", "session.away", "session.back",
        "flow.finished", "flow.failed",
        "file.created", "file.changed", "file.deleted",
    ];

    /// <summary>
    /// What a trigger's <c>only</c> may say (T20.3): context-free machine conditions the runtime reads when the
    /// trigger fires. A step's "Only if" needs a step's device or program, a trigger has none, so these stand alone.
    /// </summary>
    public static readonly IReadOnlyList<string> Conditions = ["onBattery", "onAC"];

    /// <summary>
    /// Whether a schedule can ever be due and is one the runtime may register: the validator's schedule rules in one
    /// place, so the runtime skips exactly what the validator calls ignored.
    /// </summary>
    public static bool IsValidSchedule(ScheduleTrigger rule)
    {
        if (rule.Only is not null || rule.Grace < TimeSpan.Zero)
            return false;
        return rule switch
        {
            AtTrigger a => a.Days != Days.None,
            EveryTrigger e => e.Interval >= EveryTrigger.MinInterval && e.Interval <= EveryTrigger.MaxInterval
                && (e.From is null) == (e.To is null) && (e.From is null || e.From != e.To),
            _ => true,
        };
    }

    public static bool IsCondition(string? name) => name is not null && Conditions.Contains(name);

    /// <summary><c>network.connected</c> and <c>network.disconnected</c>: the events <c>via</c> and <c>ssid</c> narrow.</summary>
    public static bool IsNetworkEvent(string? name) => name is "network.connected" or "network.disconnected";

    /// <summary><c>device.added</c> and <c>device.removed</c>: the events whose <c>device</c> is a part of a name.</summary>
    public static bool IsDeviceEvent(string? name) => name is "device.added" or "device.removed";

    /// <summary><c>audio.default-changed</c>: the event whose <c>device</c> is a Library audio device.</summary>
    public static bool IsAudioEvent(string? name) => name == "audio.default-changed";

    /// <summary><c>flow.finished</c> and <c>flow.failed</c> (T26.3): raised by a flow's own run ending, in this process.</summary>
    public static bool IsFlowEvent(string? name) => name is "flow.finished" or "flow.failed";

    /// <summary>A <c>flow:</c> filter as every flow reference reads it: trimmed, and empty means any flow.</summary>
    public static string? FlowFilter(string? flow) => string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

    /// <summary>Why <c>flow</c> can't stand on this event of <paramref name="ownFlowId"/>, or null; the reader, validator and runtime say the same.</summary>
    public static string? FlowFilterProblem(string name, string? flow, string? ownFlowId = null) =>
        FlowFilter(flow) is not { } id ? null
        : !IsFlowEvent(name) ? "flow only goes with flow.finished or flow.failed."
        : id == ownFlowId ? "A flow can't wait for itself to end."
        : null;

    /// <summary><c>file.created</c>, <c>file.changed</c> and <c>file.deleted</c> (T26.5): a change directly inside a watched folder.</summary>
    public static bool IsFileEvent(string? name) => name is "file.created" or "file.changed" or "file.deleted";

    /// <summary>Longest <c>filter:</c>; the match is linear in name times pattern, so a long pattern can't be made to run away.</summary>
    public const int MaxFilterLength = 128;

    /// <summary>A <c>path:</c> or <c>filter:</c> as the reader keeps it: trimmed, and empty means none.</summary>
    public static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// The full folder a <c>path:</c> names: environment variables expanded, no <c>..</c>, a full path (drive or share), no
    /// trailing separator. Null with <paramref name="problem"/> when it can't be one. The reader, validator and runtime all go
    /// through this, so each says the same (the folder need not exist: that is a validator warning, not a refusal).
    /// </summary>
    public static string? ResolveFolder(string? path, out string? problem)
    {
        problem = null;
        if (Trimmed(path) is not { } written)
        {
            problem = "path needs the folder to watch (like %USERPROFILE%\\Downloads).";
            return null;
        }
        var expanded = Environment.ExpandEnvironmentVariables(written.Trim('"'));
        if (expanded.Contains('%'))
        {
            problem = $"{written} uses a % name that isn't set on this PC.";
            return null;
        }
        if (expanded.Split('\\', '/').Any(part => part.Trim() == ".."))
        {
            problem = "path can't use .. (name the folder in full).";
            return null;
        }
        if (expanded.StartsWith(@"\\.\", StringComparison.Ordinal) || expanded.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            problem = "path can't be a device path; use a drive or share folder.";
            return null;
        }
        if (!System.IO.Path.IsPathFullyQualified(expanded))
        {
            problem = "path needs a full folder name, starting with a drive (like C:\\Users\\you\\Downloads).";
            return null;
        }
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(expanded));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            problem = "path isn't a folder name Windows accepts.";
            return null;
        }
    }

    /// <summary>Why <c>path</c> / <c>filter</c> can't stand on this event, or null; the reader, validator and runtime say the same.</summary>
    public static string? FileFilterProblem(string name, string? path, string? filter)
    {
        if (!IsFileEvent(name))
            return Trimmed(path) is not null || Trimmed(filter) is not null ? "path and filter only go with file.created, file.changed or file.deleted." : null;
        if (ResolveFolder(path, out var problem) is null)
            return problem;
        if (Trimmed(filter) is not { } pattern)
            return null;
        return pattern.Length > MaxFilterLength ? $"filter is at most {MaxFilterLength} characters."
            : pattern.AsSpan().IndexOfAny('\\', '/') >= 0 ? "filter is a file name pattern like *.pdf, not a path (the folder goes in path)."
            : null;
    }

    /// <summary>Whether a file name fits a <c>filter:</c> (<c>*</c> any run, <c>?</c> one character, any case; null or empty = every name). One scan with one backtrack point, never a regex.</summary>
    public static bool FilterMatches(string? filter, string name)
    {
        if (string.IsNullOrEmpty(filter))
            return true;
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < filter.Length && filter[p] != '*' && (filter[p] == '?' || char.ToUpperInvariant(filter[p]) == char.ToUpperInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < filter.Length && filter[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
                return false;
        }
        while (p < filter.Length && filter[p] == '*')
            p++;
        return p == filter.Length;
    }

    /// <summary>Why <c>device</c> can't stand on this event, or null.</summary>
    public static string? DeviceFilterProblem(string name, string? device) =>
        device is not null && !IsDeviceEvent(name) && !IsAudioEvent(name)
            ? "device only goes with device.added, device.removed or audio.default-changed." : null;

    /// <summary>The YAML and event word of a connection kind: "wifi", "cable".</summary>
    public static string ViaText(NetworkVia via) => via == NetworkVia.WiFi ? "wifi" : "cable";

    public static bool TryParseVia(string? text, out NetworkVia via)
    {
        via = default;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "wifi" or "wi-fi":
                via = NetworkVia.WiFi;
                return true;
            case "cable" or "ethernet":
                via = NetworkVia.Cable;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Why a network filter can't stand on this event, or null: the reader and the validator say the same.</summary>
    public static string? NetworkFilterProblem(string name, NetworkVia? via, string? ssid) =>
        (via is not null || ssid is not null) && !IsNetworkEvent(name) ? $"{(ssid is not null ? "ssid" : "via")} only goes with network.connected or network.disconnected."
        : ssid is not null && via == NetworkVia.Cable ? "ssid names a Wi-Fi network, so it can't go with via: cable."
        : null;

    /// <summary>History wording of an event: "Event · PC unlocked".</summary>
    public static string EventLabel(string name) => name switch
    {
        "session.lock" => "PC locked",
        "session.unlock" => "PC unlocked",
        "session.logon" => "signed in",
        "session.logoff" => "signed out",
        "power.resume" => "PC woke up",
        "power.ac" => "plugged in",
        "power.battery" => "on battery",
        "device.added" => "device connected",
        "device.removed" => "device disconnected",
        "audio.default-changed" => "default sound device changed",
        "display.changed" => "screens changed",
        "network.connected" => "network connected",
        "network.disconnected" => "network disconnected",
        "display.off" => "screens turned off",
        "display.on" => "screens turned on",
        "session.away" => "you stepped away",
        "session.back" => "you came back",
        "flow.finished" => "a flow finished",
        "flow.failed" => "a flow failed",
        "file.created" => "a file appeared",
        "file.changed" => "a file changed",
        "file.deleted" => "a file was removed",
        _ => name,
    };

    private static readonly string[] ShortDays = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly string[] LongDays = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    private static readonly string[] TimeFormats = ["H:mm", "HH:mm"];

    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss"];

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static bool TryParseDateTime(string? text, out DateTime when) =>
        DateTime.TryParseExact(text?.Trim(), DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out when);

    public static string FormatDateTime(DateTime when) => when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"daily", "weekdays", "weekends" or one weekday name (mon or monday).</summary>
    public static bool TryParseDays(string? text, out Days days)
    {
        days = Days.None;
        var name = text?.Trim().ToLowerInvariant();
        switch (name)
        {
            case "daily":
                days = Days.Daily;
                return true;
            case "weekdays":
                days = Days.Weekdays;
                return true;
            case "weekends":
                days = Days.Weekends;
                return true;
        }
        var index = Array.IndexOf(ShortDays, name);
        if (index < 0)
            index = Array.IndexOf(LongDays, name);
        if (index < 0)
            return false;
        days = (Days)(1 << index);
        return true;
    }

    /// <summary>A list of day names or keywords, unioned; false when empty or any entry is unknown.</summary>
    public static bool TryParseDays(IEnumerable<string> names, out Days days)
    {
        days = Days.None;
        foreach (var name in names)
        {
            if (!TryParseDays(name, out var one))
                return false;
            days |= one;
        }
        return days != Days.None;
    }

    /// <summary>One keyword for daily / weekdays / weekends, else the weekday names in order.</summary>
    public static IReadOnlyList<string> FormatDays(Days days) => days switch
    {
        Days.Daily => ["daily"],
        Days.Weekdays => ["weekdays"],
        Days.Weekends => ["weekends"],
        _ => [.. ShortDays.Where((_, i) => days.HasFlag((Days)(1 << i)))],
    };

    public static bool IsEvent(string? name) => name is not null && Events.Contains(name);

    /// <summary>Human text for history and the editor: "Schedule · 22:30", "Event · PC unlocked".</summary>
    public static string Describe(Trigger trigger) => trigger switch
    {
        AtTrigger at => $"Schedule · {FormatTime(at.Time)}",
        EveryTrigger every => $"Schedule · every {Duration.Format(every.Interval)}",
        OnceTrigger once => $"Schedule · {FormatDateTime(once.When)}",
        EngineStartTrigger => "Event · engine start",
        EventTrigger e => "Event · " + EventLabel(e.Name),
        _ => "Trigger",
    };
}
