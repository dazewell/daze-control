using System.Globalization;
using DazeControl.Core.Model;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>
/// The words and pure edits of the "When this runs" card (M20 T20.6, DESIGN §5, D-107): a plain sentence for every
/// trigger, the "Next: today 22:30" line, and one function per picker. Pure, so every sentence and edit is tested
/// without a window or a clock. UX rule: never "trigger", "cron" or "node" in what the person reads.
/// </summary>
public static class WhenThisRuns
{
    /// <summary>The kind picker's rows, in <see cref="KindOf"/> order.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["At a time", "Every so often", "Once", "When something happens"];

    public const int AtKind = 0, EveryKind = 1, OnceKind = 2, EventKind = 3;

    /// <summary>The "Only" picker's rows; index 0 = no condition. Ids come from <see cref="TriggerText.Conditions"/>.</summary>
    public static IReadOnlyList<string> OnlyNames { get; } = ["Always", "Only on battery", "Only when plugged in"];

    private static readonly string[] OnlyIds = ["", "onBattery", "onAC"];

    public const int GraceDefaultMinutes = 10;

    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>What an event name picker lists: "When the PC is unlocked". The names come from the one list in Core.</summary>
    public static IReadOnlyList<EventChoice> EventChoices { get; } =
        [new(EngineStartTrigger.Name, Event(EngineStartTrigger.Name)), .. TriggerText.Events.Select(n => new EventChoice(n, Event(n)))];

    /// <summary>The "Add events" flyout's groups; an event of a source added later lands in "Other".</summary>
    public static IReadOnlyList<EventGroup> EventGroups { get; } = BuildGroups();

    public sealed record EventGroup(string Title, IReadOnlyList<EventChoice> Events);

    private static List<EventGroup> BuildGroups()
    {
        (string Title, string[] Prefixes)[] groups =
            [("Session", ["session."]), ("Power", ["power."]), ("Devices", ["device."]), ("Sound and screens", ["audio.", "display."]), ("Network", ["network."]), ("Flows", ["flow."])];
        var rest = TriggerText.Events.ToList();
        var result = new List<EventGroup>();
        foreach (var (title, prefixes) in groups)
        {
            var own = rest.Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))).ToList();
            rest.RemoveAll(own.Contains);
            if (own.Count > 0)
                result.Add(new EventGroup(title, [.. own.Select(n => new EventChoice(n, Event(n)))]));
        }
        if (rest.Count > 0)
            result.Add(new EventGroup("Other", [.. rest.Select(n => new EventChoice(n, Event(n)))]));
        return result;
    }

    public sealed record EventChoice(string Name, string Sentence);

    public static int KindOf(Trigger trigger) => trigger switch
    {
        AtTrigger => AtKind,
        EveryTrigger => EveryKind,
        OnceTrigger => OnceKind,
        _ => EventKind,
    };

    public static int OnlyIndex(Trigger trigger) => Math.Max(0, Array.IndexOf(OnlyIds, trigger.Only ?? ""));

    // ---- Sentences ----

    /// <summary>"Weekdays at 22:30", "Every 2 hours from 09:00 to 18:00", "When the PC is unlocked".</summary>
    /// <param name="flowName">A flow's name by its id, for "When 'Headphones' finishes"; null or unknown shows the id.</param>
    public static string Sentence(Trigger trigger, Func<string, string?>? flowName = null)
    {
        var text = trigger switch
        {
            AtTrigger at => $"{DaysText(at.Days)} at {TriggerText.FormatTime(at.Time)}",
            EveryTrigger every => Every(every),
            OnceTrigger once => OnceText(once.When),
            EventTrigger e when TriggerText.IsFlowEvent(e.Name) && TriggerText.FlowFilter(e.Flow) is { } id =>
                $"When '{flowName?.Invoke(id) ?? id}' {(e.Name == "flow.failed" ? "fails" : "finishes")}",
            EventTrigger e => TriggerText.IsNetworkEvent(e.Name) ? Network(e) : e.Device is { Length: > 0 } ? Device(e) : Event(e.Name),
            EngineStartTrigger => Event(EngineStartTrigger.Name),
            _ => "Starts by itself",
        };
        return trigger is not ScheduleTrigger && trigger.Only is { Length: > 0 } only && Array.IndexOf(OnlyIds, only) is > 0 and var i
            ? $"{text}, {OnlyNames[i].ToLowerInvariant()}"
            : text;
    }

    public static string Event(string name) => name switch
    {
        EngineStartTrigger.Name => "When Daze Control starts",
        "session.lock" => "When the PC is locked",
        "session.unlock" => "When the PC is unlocked",
        "session.logon" => "When you sign in",
        "session.logoff" => "When you sign out",
        "power.resume" => "When the PC wakes up",
        "power.ac" => "When the PC is plugged in",
        "power.battery" => "When the PC runs on battery",
        "device.added" => "When a device is connected",
        "device.removed" => "When a device is disconnected",
        "audio.default-changed" => "When the default sound device changes",
        "display.changed" => "When the screens change",
        "network.connected" => "When the network connects",
        "network.disconnected" => "When the network disconnects",
        "display.off" => "When the screens turn off",
        "display.on" => "When the screens turn on",
        "session.away" => "When you step away",
        "session.back" => "When you come back",
        "flow.finished" => "When a flow finishes",
        "flow.failed" => "When a flow fails",
        _ => "When " + TriggerText.EventLabel(name), // a source added later reads passably until its sentence is written here
    };

    /// <summary>The network picker's rows (index 0 = any connection); a Wi-Fi network's name goes in its own box.</summary>
    public static IReadOnlyList<string> ConnectionNames { get; } = ["Any connection", "Wi-Fi", "Cable"];

    public const int AnyConnection = 0, WiFiConnection = 1, CableConnection = 2;

    /// <summary>"When a cable is plugged in", "When Wi-Fi 'Home' connects"; any connection keeps the event's own sentence.</summary>
    public static string Network(EventTrigger e)
    {
        var connected = e.Name == "network.connected";
        if (e.Ssid is { Length: > 0 } ssid)
            return $"When Wi-Fi '{ssid}' {(connected ? "connects" : "disconnects")}";
        return e.Via switch
        {
            NetworkVia.WiFi => connected ? "When Wi-Fi connects" : "When Wi-Fi disconnects",
            NetworkVia.Cable => connected ? "When a cable is plugged in" : "When a cable is unplugged",
            _ => Event(e.Name),
        };
    }

    /// <summary>"When 'Xbox' is connected", "When the default sound device changes to 'Headphones'" (a Library key).</summary>
    public static string Device(EventTrigger e) => e.Name switch
    {
        "device.added" => $"When '{e.Device}' is connected",
        "device.removed" => $"When '{e.Device}' is disconnected",
        _ => $"When the default sound device changes to '{e.Device}'",
    };

    /// <summary>The text a device event looks for (empty = any device); null on an event with no device choice.</summary>
    public static Trigger WithDevice(Trigger t, string? device)
    {
        if (t is not EventTrigger e || !(TriggerText.IsDeviceEvent(e.Name) || TriggerText.IsAudioEvent(e.Name)))
            return t;
        return e with { Device = string.IsNullOrWhiteSpace(device) ? null : device.Trim() };
    }

    /// <summary>The flow a flow event waits for, by id (null or empty = any flow); other events keep the trigger as it is.</summary>
    public static Trigger WithFlow(Trigger t, string? flowId)
    {
        if (t is not EventTrigger e || !TriggerText.IsFlowEvent(e.Name))
            return t;
        return e with { Flow = TriggerText.FlowFilter(flowId) };
    }

    /// <summary>A Wi-Fi name means Wi-Fi even without <c>via</c> (a hand-written <c>ssid:</c>).</summary>
    public static int ConnectionIndex(Trigger t) => t switch
    {
        EventTrigger { Ssid: not null } or EventTrigger { Via: NetworkVia.WiFi } => WiFiConnection,
        EventTrigger { Via: NetworkVia.Cable } => CableConnection,
        _ => AnyConnection,
    };

    public static string DaysText(Days days) => days switch
    {
        Days.Daily => "Every day",
        Days.Weekdays => "Weekdays",
        Days.Weekends => "Weekends",
        _ => Join(DayNames.Select((n, i) => (n, i)).Where(p => days.HasFlag((Days)(1 << p.i))).Select(p => Plural(p.n)).ToList()),
    };

    private static string Plural(string shortName) => shortName switch
    {
        "Mon" => "Mondays",
        "Tue" => "Tuesdays",
        "Wed" => "Wednesdays",
        "Thu" => "Thursdays",
        "Fri" => "Fridays",
        "Sat" => "Saturdays",
        _ => "Sundays",
    };

    private static string Join(List<string> parts) => parts.Count switch
    {
        0 => "No days",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    private static string Every(EveryTrigger every)
    {
        var text = "Every " + IntervalText(every.Interval);
        return every.From is { } from && every.To is { } to
            ? $"{text} from {TriggerText.FormatTime(from)} to {TriggerText.FormatTime(to)}"
            : text;
    }

    /// <summary>"minute", "2 hours", "90 minutes" (whole hours say hours).</summary>
    public static string IntervalText(TimeSpan interval)
    {
        if (interval.Ticks % TimeSpan.TicksPerHour == 0)
        {
            var hours = (int)interval.TotalHours;
            return hours == 1 ? "hour" : $"{hours} hours";
        }
        var minutes = (int)Math.Round(interval.TotalMinutes);
        return minutes == 1 ? "minute" : $"{minutes} minutes";
    }

    private static string OnceText(DateTime when) =>
        $"Once, {when.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} at {TriggerText.FormatTime(TimeOnly.FromDateTime(when))}";

    /// <summary>"Next: today 22:30", "tomorrow 09:00", "Mon 22:30", "3 Dec 22:30"; "Never" when the schedule has no due time.</summary>
    public static string NextText(DateTimeOffset? next, DateTimeOffset now)
    {
        if (next is not { } at)
            return "Next: never";
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = TriggerText.FormatTime(TimeOnly.FromDateTime(local.DateTime));
        var days = (local.Date - today).Days;
        var day = days switch
        {
            0 => "today",
            1 => "tomorrow",
            < 7 => local.ToString("ddd", CultureInfo.InvariantCulture),
            _ => local.ToString("d MMM", CultureInfo.InvariantCulture),
        };
        return $"Next: {day} {time}";
    }

    /// <summary>The Flows list chip: "Schedule" (clock), "Event" (bolt), both, or nothing. Tooltip lists every sentence.</summary>
    public static string ChipTip(IReadOnlyList<Trigger> triggers) => string.Join("; ", triggers.Select(t => Sentence(t)));

    public static bool HasSchedule(IReadOnlyList<Trigger> triggers) => triggers.Any(t => t is ScheduleTrigger);

    public static bool HasEvent(IReadOnlyList<Trigger> triggers) => triggers.Any(t => t is not ScheduleTrigger);

    // ---- Edits (each returns the changed trigger; nothing else about it moves) ----

    /// <summary>A new trigger of the kind. A schedule gets its missed-run choice now (D-107: no global default): run, up to 10 minutes late.</summary>
    public static Trigger Create(int kind, DateTimeOffset now) => kind switch
    {
        AtKind => new AtTrigger(new TimeOnly(9, 0), Days.Daily) { IfMissed = IfMissed.Run, Grace = TimeSpan.FromMinutes(GraceDefaultMinutes) },
        EveryKind => new EveryTrigger(TimeSpan.FromHours(1)) { IfMissed = IfMissed.Run, Grace = TimeSpan.FromMinutes(GraceDefaultMinutes) },
        OnceKind => new OnceTrigger(now.ToLocalTime().Date.AddDays(1).AddHours(9)) { IfMissed = IfMissed.Run, Grace = TimeSpan.FromMinutes(GraceDefaultMinutes) },
        _ => new EventTrigger("session.unlock"),
    };

    /// <summary>Another kind: a fresh trigger of it that keeps the old one's missed-run choice and condition where they apply.</summary>
    public static Trigger ChangeKind(Trigger old, int kind, DateTimeOffset now)
    {
        if (KindOf(old) == kind)
            return old;
        var made = Create(kind, now);
        if (made is ScheduleTrigger schedule && old is ScheduleTrigger before)
            made = schedule with { IfMissed = before.IfMissed, Grace = before.Grace };
        return kind == EventKind ? made with { Only = old.Only } : made with { Only = null };
    }

    public static Trigger WithTime(Trigger t, TimeOnly time) => t is AtTrigger at ? at with { Time = time } : t;

    /// <summary>Flips one weekday; the last day can't be switched off (a schedule needs one).</summary>
    public static Trigger ToggleDay(Trigger t, int dayIndex)
    {
        if (t is not AtTrigger at || dayIndex is < 0 or > 6)
            return t;
        var flipped = at.Days ^ (Days)(1 << dayIndex);
        return flipped == Days.None ? t : at with { Days = flipped };
    }

    public static Trigger WithDays(Trigger t, Days days) => t is AtTrigger at && days != Days.None ? at with { Days = days } : t;

    public static Trigger WithInterval(Trigger t, TimeSpan interval) =>
        t is EveryTrigger e ? e with { Interval = interval < EveryTrigger.MinInterval ? EveryTrigger.MinInterval : interval } : t;

    /// <summary>Turns the window on (09:00 to 18:00) or off.</summary>
    public static Trigger WithWindow(Trigger t, bool on) => t is not EveryTrigger e ? t
        : on ? e with { From = e.From ?? new TimeOnly(9, 0), To = e.To ?? new TimeOnly(18, 0) }
        : e with { From = null, To = null };

    /// <summary>A window end equal to its start is refused (the validator would ignore the schedule): the end moves a minute on.</summary>
    public static Trigger WithWindowTimes(Trigger t, TimeOnly? from, TimeOnly? to)
    {
        if (t is not EveryTrigger e || e.From is null)
            return t;
        var start = from ?? e.From.Value;
        var end = to ?? e.To ?? start;
        if (start == end)
            end = end.AddMinutes(1);
        return e with { From = start, To = end };
    }

    public static Trigger WithWhen(Trigger t, DateTime when) => t is OnceTrigger ? new OnceTrigger(when) { IfMissed = ((OnceTrigger)t).IfMissed, Grace = ((OnceTrigger)t).Grace, Only = t.Only } : t;

    /// <summary>Another event; connected and disconnected swap with their connection choice kept.</summary>
    public static Trigger WithEvent(Trigger t, string name) =>
        name == EngineStartTrigger.Name ? new EngineStartTrigger { Only = t.Only }
        : t is EventTrigger e && TriggerText.IsNetworkEvent(e.Name) && TriggerText.IsNetworkEvent(name) ? e with { Name = name }
        : t is EventTrigger d && TriggerText.IsDeviceEvent(d.Name) && TriggerText.IsDeviceEvent(name) ? d with { Name = name }
        : t is EventTrigger f && TriggerText.IsFlowEvent(f.Name) && TriggerText.IsFlowEvent(name) ? f with { Name = name }
        : t is EventTrigger or EngineStartTrigger ? new EventTrigger(name) { Only = t.Only }
        : t;

    /// <summary>Any connection, Wi-Fi or cable; leaving Wi-Fi drops the network name.</summary>
    public static Trigger WithConnection(Trigger t, int index)
    {
        if (t is not EventTrigger e || !TriggerText.IsNetworkEvent(e.Name) || ConnectionIndex(t) == index)
            return t;
        return index switch
        {
            WiFiConnection => e with { Via = NetworkVia.WiFi },
            CableConnection => e with { Via = NetworkVia.Cable, Ssid = null },
            _ => e with { Via = null, Ssid = null },
        };
    }

    /// <summary>A Wi-Fi network's name (empty = any Wi-Fi); a name makes the trigger Wi-Fi.</summary>
    public static Trigger WithNetworkName(Trigger t, string? name)
    {
        if (t is not EventTrigger e || !TriggerText.IsNetworkEvent(e.Name))
            return t;
        var ssid = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return ssid is null ? e with { Ssid = null } : e with { Ssid = ssid, Via = NetworkVia.WiFi };
    }

    public static Trigger WithOnly(Trigger t, int index) =>
        t is ScheduleTrigger ? t : t with { Only = index > 0 && index < OnlyIds.Length ? OnlyIds[index] : null };

    public static Trigger WithIfMissed(Trigger t, IfMissed choice) => t is ScheduleTrigger s ? s with { IfMissed = choice } : t;

    /// <summary>"Run it if it is less than N minutes late"; 0..1440. Only <see cref="IfMissed.Run"/> uses it.</summary>
    public static Trigger WithGrace(Trigger t, int minutes) =>
        t is ScheduleTrigger s ? s with { Grace = TimeSpan.FromMinutes(Math.Clamp(minutes, 0, 1440)) } : t;
}
