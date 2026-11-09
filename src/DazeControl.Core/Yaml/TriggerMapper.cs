using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Core.Yaml;

/// <summary>
/// Maps a flow's <c>triggers:</c> (M20). A trigger that can't be understood is dropped with a warning on its flow, so the
/// flow stays and its other triggers keep working (D-021). Warnings, not errors: errors would disable the whole flow.
/// </summary>
internal static class TriggerMapper
{
    public static ValueList<Trigger> Read(List<ParamDocument>? documents, string path, string flowId, ICollection<Problem> problems)
    {
        var triggers = new List<Trigger>();
        for (var i = 0; i < (documents?.Count ?? 0); i++)
        {
            var itemPath = $"{path}[{i}]";
            string? why = null;
            Trigger? trigger;
            if (documents![i]?.ToValue() is MapParam map && ToDocument(map, out why) is { } document)
                (trigger, why) = ReadOne(document);
            else
                (trigger, why) = (null, why ?? "A trigger is a mapping, like { at: \"22:30\" }.");
            if (trigger is null)
                problems.Add(new Problem(Severity.Warning, itemPath, why + " This trigger is ignored.", flowId));
            else
                triggers.Add(trigger);
        }
        return new(triggers);
    }

    private static TriggerDocument? ToDocument(MapParam map, out string? why)
    {
        why = null;
        var d = new TriggerDocument();
        foreach (var (key, value) in map.Entries)
        {
            if (key == "days")
            {
                d.Days = new ParamDocument(value);
                continue;
            }
            if (key == "between")
            {
                d.Between = new ParamDocument(value);
                continue;
            }
            if (value is not ScalarParam scalar)
            {
                why = $"{key} must be a single value.";
                return null;
            }
            switch (key)
            {
                case "at": d.At = scalar.Text; break;
                case "every": d.Every = scalar.Text; break;
                case "once": d.Once = scalar.Text; break;
                case "on": d.On = scalar.Text; break;
                case "only": d.Only = scalar.Text; break;
                case "ifMissed": d.IfMissed = scalar.Text; break;
                case "grace": d.Grace = scalar.Text; break;
            }
        }
        return d;
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
        if (d.Days is not null && d.At is null)
            return (null, "days only goes with at.");
        if (d.Between is not null && d.Every is null)
            return (null, "between only goes with every.");

        if (d.On is not null)
        {
            var name = d.On.Trim();
            if (name == EngineStartTrigger.Name)
                return (new EngineStartTrigger { Only = only }, null);
            return TriggerText.IsEvent(name)
                ? (new EventTrigger(name) { Only = only }, null)
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
            if (!Duration.TryParse(d.Every, out var interval) || interval < EveryTrigger.MinInterval)
                error = $"\"{d.Every}\" isn't an interval of at least a minute (like 2h or 30m).";
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

    public static ParamDocument ToParam(Trigger trigger)
    {
        var d = ToDocument(trigger);
        var entries = new List<KeyValuePair<string, ParamValue>>();
        void Add(string key, string? text)
        {
            if (text is not null)
                entries.Add(new(key, new ScalarParam(text)));
        }
        Add("at", d.At);
        if (d.Days is not null)
            entries.Add(new("days", d.Days.ToValue()));
        Add("every", d.Every);
        if (d.Between is not null)
            entries.Add(new("between", d.Between.ToValue()));
        Add("once", d.Once);
        Add("on", d.On);
        Add("only", d.Only);
        Add("ifMissed", d.IfMissed);
        Add("grace", d.Grace);
        return new ParamDocument(new MapParam(new(entries)));
    }

    private static TriggerDocument ToDocument(Trigger trigger)
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
            EventTrigger e => new TriggerDocument { On = e.Name },
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

/// <summary>A trigger's keys as text, before checking (not itself a YAML shape: see <see cref="ParamDocument"/>).</summary>
internal sealed class TriggerDocument
{
    public string? At { get; set; }

    public ParamDocument? Days { get; set; }

    public string? Every { get; set; }

    public ParamDocument? Between { get; set; }

    public string? Once { get; set; }

    public string? On { get; set; }

    public string? Only { get; set; }

    public string? IfMissed { get; set; }

    public string? Grace { get; set; }
}
