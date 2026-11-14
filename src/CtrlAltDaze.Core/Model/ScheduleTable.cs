namespace CtrlAltDaze.Core.Model;

/// <summary>A schedule rule's next due time; <see cref="Index"/> points into the rules given to <see cref="ScheduleTable.Next(IReadOnlyList{ScheduleTrigger}, DateTimeOffset, TimeProvider)"/>.</summary>
public readonly record struct ScheduleDue(int Index, DateTimeOffset At);

/// <summary>
/// Pure next-due-time arithmetic for schedule triggers (M20, DESIGN §2). Local time comes from
/// <see cref="TimeProvider.LocalTimeZone"/>, so tests pick the zone. A wall-clock time that doesn't exist (spring-forward
/// gap) runs once, shifted by the gap; one that happens twice (fall-back overlap) runs at its first occurrence only.
/// </summary>
public static class ScheduleTable
{
    /// <summary>The earliest due time strictly after <paramref name="now"/> over all rules (ties: the lower index), or null when none is left.</summary>
    public static ScheduleDue? Next(IReadOnlyList<ScheduleTrigger> rules, DateTimeOffset now, TimeProvider time)
    {
        ScheduleDue? best = null;
        for (var i = 0; i < rules.Count; i++)
        {
            if (Next(rules[i], now, time) is { } at && (best is null || at < best.Value.At))
                best = new ScheduleDue(i, at);
        }
        return best;
    }

    /// <summary>The first due time of <paramref name="rule"/> strictly after <paramref name="after"/>, or null.</summary>
    public static DateTimeOffset? Next(ScheduleTrigger rule, DateTimeOffset after, TimeProvider time)
    {
        var zone = time.LocalTimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        switch (rule)
        {
            case AtTrigger at:
                for (var d = 0; d <= 8; d++)
                {
                    var date = today.AddDays(d);
                    if (!at.Days.HasFlag((Days)(1 << (((int)date.DayOfWeek + 6) % 7))))
                        continue;
                    var due = Resolve(date, at.Time, zone);
                    if (due > after)
                        return due;
                }
                return null;
            case OnceTrigger once:
                var when = Resolve(DateOnly.FromDateTime(once.When), TimeOnly.FromDateTime(once.When), zone);
                return when > after ? when : null;
            case EveryTrigger every:
                return NextEvery(every, after, today, zone);
            default:
                return null;
        }
    }

    private static DateTimeOffset? NextEvery(EveryTrigger every, DateTimeOffset after, DateOnly today, TimeZoneInfo zone)
    {
        if (every.Interval < EveryTrigger.MinInterval)
            return null;
        var windowed = every is { From: not null, To: not null };
        var from = every.From ?? TimeOnly.MinValue;
        var to = every.To ?? TimeOnly.MinValue;
        for (var d = -1; d <= 2; d++)
        {
            var date = today.AddDays(d);
            var start = Resolve(date, from, zone);
            // Windowed: end included, and an end at or before the start is the next day's. Unwindowed: next midnight, excluded.
            var end = Resolve(windowed && to > from ? date : date.AddDays(1), to, zone);
            var k = after < start ? 0 : (after - start).Ticks / every.Interval.Ticks + 1;
            var due = start + every.Interval * k;
            if (windowed ? due <= end : due < end)
                return due;
        }
        return null;
    }

    private static DateTimeOffset Resolve(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // In the gap: the offset from before it puts the time just after the gap.
            var before = zone.GetUtcOffset(local.AddDays(-1));
            return new DateTimeOffset(local, before).ToOffset(zone.GetUtcOffset(new DateTimeOffset(local, before)));
        }
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max());
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
