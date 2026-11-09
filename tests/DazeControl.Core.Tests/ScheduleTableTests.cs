using System.Globalization;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class ScheduleTableTests
{
    private sealed class ZonedTime(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static readonly TimeProvider Time = new ZonedTime(FindEastern());

    private static TimeZoneInfo FindEastern()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }

    // "2026-06-10 22:30 -4" = that wall time at that UTC offset.
    private static DateTimeOffset T(string text)
    {
        var parts = text.Split(' ');
        return new DateTimeOffset(DateTime.Parse(parts[0] + " " + parts[1], CultureInfo.InvariantCulture), TimeSpan.FromHours(int.Parse(parts[2], CultureInfo.InvariantCulture)));
    }

    private static DateTimeOffset? Next(ScheduleTrigger rule, string now) => ScheduleTable.Next(rule, T(now), Time);

    private static TimeOnly Clock(string text) => TimeOnly.Parse(text, CultureInfo.InvariantCulture);

    private static AtTrigger At(string time, Days days = Days.Daily) => new(Clock(time), days);

    private static EveryTrigger Every(string interval, string? from = null, string? to = null)
    {
        Assert.True(Duration.TryParse(interval, out var span));
        return from is null ? new(span) : new(span, Clock(from), Clock(to!));
    }

    [Fact]
    public void DailyTimeLaterToday()
    {
        Assert.Equal(T("2026-06-10 22:30 -4"), Next(At("22:30"), "2026-06-10 10:00 -4"));
    }

    [Fact]
    public void DueTimeIsStrictlyAfterNow()
    {
        Assert.Equal(T("2026-06-11 22:30 -4"), Next(At("22:30"), "2026-06-10 22:30 -4"));
    }

    [Fact]
    public void WeekdaysSkipTheWeekend()
    {
        // 2026-06-12 is a Friday.
        Assert.Equal(T("2026-06-15 09:00 -4"), Next(At("09:00", Days.Weekdays), "2026-06-12 23:00 -4"));
        Assert.Equal(T("2026-06-13 09:00 -4"), Next(At("09:00", Days.Weekends), "2026-06-12 23:00 -4"));
        Assert.Equal(T("2026-06-17 09:00 -4"), Next(At("09:00", Days.Wed), "2026-06-10 09:00 -4"));
    }

    [Fact]
    public void YearAndMonthEndsRollOver()
    {
        Assert.Equal(T("2027-01-01 00:30 -5"), Next(At("00:30"), "2026-12-31 23:00 -5"));
        Assert.Equal(T("2027-03-01 08:00 -5"), Next(At("08:00"), "2027-02-28 12:00 -5"));
        Assert.Equal(T("2028-02-29 08:00 -5"), Next(At("08:00"), "2028-02-28 12:00 -5"));
    }

    [Fact]
    public void SpringForwardGapRunsOnceJustAfterTheGap()
    {
        // 02:30 doesn't exist on 2026-03-08; it runs at 03:30 EDT, the next day it's 02:30 again.
        var first = Next(At("02:30"), "2026-03-07 12:00 -5");
        Assert.Equal(T("2026-03-08 03:30 -4"), first);
        Assert.Equal(T("2026-03-09 02:30 -4"), ScheduleTable.Next(At("02:30"), first!.Value, Time));
    }

    [Fact]
    public void FallBackOverlapRunsAtTheFirstOccurrenceOnly()
    {
        var first = Next(At("01:30"), "2026-10-31 12:00 -4");
        Assert.Equal(T("2026-11-01 01:30 -4"), first);
        Assert.Equal(T("2026-11-02 01:30 -5"), ScheduleTable.Next(At("01:30"), first!.Value, Time));
        // Past the first occurrence but still inside the repeated hour: not again.
        Assert.Equal(T("2026-11-02 01:30 -5"), Next(At("01:30"), "2026-11-01 01:45 -5"));
    }

    [Fact]
    public void IntervalWithoutAWindowCountsFromMidnight()
    {
        Assert.Equal(T("2026-06-10 12:00 -4"), Next(Every("2h"), "2026-06-10 10:30 -4"));
        Assert.Equal(T("2026-06-11 00:00 -4"), Next(Every("2h"), "2026-06-10 22:30 -4"));
        Assert.Equal(T("2026-06-11 00:00 -4"), Next(Every("5h"), "2026-06-10 21:00 -4"));
    }

    [Fact]
    public void IntervalWithAWindowStartsAtItsFrom()
    {
        var rule = Every("2h", "09:00", "18:00");
        Assert.Equal(T("2026-06-10 09:00 -4"), Next(rule, "2026-06-10 08:00 -4"));
        Assert.Equal(T("2026-06-10 17:00 -4"), Next(rule, "2026-06-10 16:59 -4"));
        Assert.Equal(T("2026-06-11 09:00 -4"), Next(rule, "2026-06-10 17:00 -4"));
        Assert.Equal(T("2026-06-11 09:00 -4"), Next(rule, "2026-06-10 17:30 -4"));
    }

    [Fact]
    public void FallBackDayIntervalRunsRepeatedHourOnce()
    {
        // 2026-11-01: 01:00 happens twice. Hourly from midnight: 00:00 EDT, 01:00 EDT (first), 02:00 EST.
        var rule = Every("1h");
        Assert.Equal(T("2026-11-01 01:00 -4"), Next(rule, "2026-11-01 00:00 -4"));
        Assert.Equal(T("2026-11-01 02:00 -5"), Next(rule, "2026-11-01 01:30 -4"));
        Assert.Equal(T("2026-11-01 02:00 -5"), Next(rule, "2026-11-01 01:10 -5"));
    }

    [Fact]
    public void WindowEndIsIncluded()
    {
        Assert.Equal(T("2026-06-10 18:00 -4"), Next(Every("3h", "09:00", "18:00"), "2026-06-10 15:00 -4"));
    }

    [Fact]
    public void WindowMayCrossMidnight()
    {
        var rule = Every("1h", "22:00", "02:00");
        Assert.Equal(T("2026-06-11 00:00 -4"), Next(rule, "2026-06-10 23:30 -4"));
        Assert.Equal(T("2026-06-11 02:00 -4"), Next(rule, "2026-06-11 01:30 -4"));
        Assert.Equal(T("2026-06-11 22:00 -4"), Next(rule, "2026-06-11 02:00 -4"));
    }

    [Fact]
    public void OnceRunsOnlyInTheFuture()
    {
        var once = new OnceTrigger(new DateTime(2026, 12, 1, 9, 30, 0, DateTimeKind.Unspecified));
        Assert.Equal(T("2026-12-01 09:30 -5"), Next(once, "2026-11-08 12:00 -5"));
        Assert.Null(Next(once, "2026-12-01 09:30 -5"));
    }

    [Fact]
    public void TheEarliestRuleWinsAndTiesTakeTheLowerIndex()
    {
        ScheduleTrigger[] rules = [At("22:30"), Every("1h"), At("11:00")];
        Assert.Equal(new ScheduleDue(1, T("2026-06-10 11:00 -4")), ScheduleTable.Next(rules, T("2026-06-10 10:30 -4"), Time));
        ScheduleTrigger[] tie = [Every("1h"), At("11:00")];
        Assert.Equal(0, ScheduleTable.Next(tie, T("2026-06-10 10:30 -4"), Time)!.Value.Index);
        Assert.Null(ScheduleTable.Next([], T("2026-06-10 10:30 -4"), Time));
    }
}
