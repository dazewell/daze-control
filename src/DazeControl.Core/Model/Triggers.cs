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
}

/// <summary><c>once: "2026-12-01 09:30"</c>, local time.</summary>
public sealed record OnceTrigger(DateTime When) : ScheduleTrigger;

/// <summary><c>on: engine.start</c>: when the engine starts (at logon).</summary>
public sealed record EngineStartTrigger : Trigger
{
    public const string Name = "engine.start";
}

/// <summary><c>on: session.unlock</c> and friends (<see cref="TriggerText.Events"/>).</summary>
public sealed record EventTrigger(string Name) : Trigger;

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
    ];

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

    /// <summary>Human text for history and the editor: "Schedule · 22:30", "Event · session.unlock".</summary>
    public static string Describe(Trigger trigger) => trigger switch
    {
        AtTrigger at => $"Schedule · {FormatTime(at.Time)}",
        EveryTrigger every => $"Schedule · every {Duration.Format(every.Interval)}",
        OnceTrigger once => $"Schedule · {FormatDateTime(once.When)}",
        EngineStartTrigger => "Event · engine start",
        EventTrigger e => "Event · " + e.Name,
        _ => "Trigger",
    };
}
