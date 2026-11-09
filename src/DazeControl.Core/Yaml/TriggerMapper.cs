using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Core.Yaml;

/// <summary>
/// Maps a flow's <c>triggers:</c> (M20). A trigger that can't be understood is dropped with a warning on its flow, so the
/// flow stays and its other triggers keep working (D-021). Warnings, not errors: errors would disable the whole flow.
/// </summary>
internal static class TriggerMapper
{
    public static ValueList<Trigger> Read(List<TriggerDocument>? documents, string path, string flowId, ICollection<Problem> problems)
    {
        var triggers = new List<Trigger>();
        for (var i = 0; i < (documents?.Count ?? 0); i++)
        {
            var itemPath = $"{path}[{i}]";
            string? why;
            Trigger? trigger;
            if (documents![i] is null)
                (trigger, why) = (null, "Empty trigger.");
            else
                (trigger, why) = ReadOne(documents[i]);
            if (trigger is null)
                problems.Add(new Problem(Severity.Warning, itemPath, why + " This trigger is ignored.", flowId));
            else
                triggers.Add(trigger);
        }
        return new(triggers);
    }

    private static (Trigger?, string?) ReadOne(TriggerDocument d)
    {
        var kinds = new[] { d.At is not null, d.Every is not null, d.Once is not null, d.On is not null }.Count(k => k);
        if (kinds != 1)
            return (null, kinds == 0 ? "Say when: at, every, once or on." : "Use one of at, every, once or on per trigger.");
        var only = string.IsNullOrWhiteSpace(d.Only) ? null : d.Only.Trim();
        var isSchedule = d.On is null;
        if (!isSchedule && (d.IfMissed is not null || d.Grace is not null))
            return (null, "ifMissed and grace only go with at, every and once.");
        if (isSchedule && only is not null)
            return (null, "only goes with on, not with a schedule.");
        if (d.Days is not null && d.At is null)
            return (null, "days only goes with at.");
        if (d.Between is not null && d.Every is null)
            return (null, "between only goes with every.");

        if (d.On is not null)
        {
            var name = d.On.Trim();
            var ssid = string.IsNullOrWhiteSpace(d.Ssid) ? null : d.Ssid.Trim();
            if (ssid is not null && name != "network.connected")
                return (null, "ssid only goes with network.connected.");
            if (name == EngineStartTrigger.Name)
                return (new EngineStartTrigger { Only = only }, null);
            return TriggerText.IsEvent(name)
                ? (new EventTrigger(name) { Only = only, Ssid = ssid }, null)
                : (null, $"\"{name}\" isn't an event I know ({string.Join(", ", TriggerText.Events)}, {EngineStartTrigger.Name}).");
        }

        if (!TryIfMissed(d.IfMissed, out var ifMissed))
            return (null, $"\"{d.IfMissed}\" isn't run or skip.");
        TimeSpan? grace = null;
        if (d.Grace is not null)
        {
            if (!Duration.TryParse(d.Grace, out var g) || g < TimeSpan.Zero)
                return (null, $"\"{d.Grace}\" isn't a duration (like 10m).");
            grace = g;
        }

        ScheduleTrigger? schedule = null;
        string? error = null;
        if (d.At is not null)
        {
            if (!TriggerText.TryParseTime(d.At, out var time))
                error = $"\"{d.At}\" isn't a time of day (like 22:30).";
            else if (!TryReadDays(d.Days?.ToValue(), out var days))
                error = "days is daily, weekdays, weekends or a list of mon, tue, wed, thu, fri, sat, sun.";
            else
                schedule = new AtTrigger(time, days);
        }
        else if (d.Every is not null)
        {
            if (!Duration.TryParse(d.Every, out var interval) || interval < EveryTrigger.MinInterval || interval > EveryTrigger.MaxInterval)
                error = $"\"{d.Every}\" isn't an interval from a minute to 24 hours (like 2h or 30m).";
            else if (d.Between is null)
                schedule = new EveryTrigger(interval);
            else if (d.Between.ToValue() is ListParam { Items: [ScalarParam a, ScalarParam b] }
                && TriggerText.TryParseTime(a.Text, out var from) && TriggerText.TryParseTime(b.Text, out var to) && from != to)
                schedule = new EveryTrigger(interval, from, to);
            else
                error = "between is two different times, like [\"09:00\", \"18:00\"].";
        }
        else if (!TriggerText.TryParseDateTime(d.Once, out var when))
        {
            error = $"\"{d.Once}\" isn't a date and time (like 2026-12-01 09:30).";
        }
        else
        {
            schedule = new OnceTrigger(when);
        }
        return schedule is null ? (null, error) : (schedule with { Only = only, IfMissed = ifMissed, Grace = grace }, null);
    }

    private static bool TryReadDays(ParamValue? value, out Days days)
    {
        days = Days.Daily;
        return value switch
        {
            null => true,
            ScalarParam s => TriggerText.TryParseDays(s.Text, out days),
            ListParam list when list.Items.All(i => i is ScalarParam) => TriggerText.TryParseDays(list.Items.Select(i => ((ScalarParam)i).Text), out days),
            _ => false,
        };
    }

    private static bool TryIfMissed(string? text, out IfMissed value)
    {
        value = IfMissed.Run;
        switch (text?.Trim().ToLowerInvariant())
        {
            case null:
            case "run":
                return true;
            case "skip":
                value = IfMissed.Skip;
                return true;
            default:
                return false;
        }
    }

    public static TriggerDocument ToDocument(Trigger trigger)
    {
        var document = trigger switch
        {
            AtTrigger at => new TriggerDocument
            {
                At = TriggerText.FormatTime(at.Time),
                Days = at.Days == Days.Daily ? null : DaysParam(at.Days),
            },
            EveryTrigger every => new TriggerDocument
            {
                Every = Duration.Format(every.Interval),
                Between = every is { From: { } from, To: { } to }
                    ? new ParamDocument(new ListParam(new([new ScalarParam(TriggerText.FormatTime(from)), new ScalarParam(TriggerText.FormatTime(to))])))
                    : null,
            },
            OnceTrigger once => new TriggerDocument { Once = TriggerText.FormatDateTime(once.When) },
            EngineStartTrigger => new TriggerDocument { On = EngineStartTrigger.Name },
            EventTrigger e => new TriggerDocument { On = e.Name, Ssid = e.Ssid },
            _ => new TriggerDocument(),
        };
        document.Only = trigger.Only;
        if (trigger is ScheduleTrigger schedule)
        {
            document.IfMissed = schedule.IfMissed == IfMissed.Run ? null : ConfigMapper.EnumText(schedule.IfMissed);
            document.Grace = Duration.Format(schedule.Grace);
        }
        return document;
    }

    private static ParamDocument DaysParam(Days days)
    {
        var names = TriggerText.FormatDays(days);
        return new ParamDocument(names.Count == 1
            ? new ScalarParam(names[0])
            : new ListParam(new(names.Select(n => (ParamValue)new ScalarParam(n)))));
    }
}
