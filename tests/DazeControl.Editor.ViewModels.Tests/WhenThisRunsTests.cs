using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>M20 T20.6: the sentences and the pure edits behind the "When this runs" card (DESIGN §5).</summary>
public sealed class WhenThisRunsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Days.Weekdays, "Weekdays at 22:30")]
    [InlineData(Days.Daily, "Every day at 22:30")]
    [InlineData(Days.Weekends, "Weekends at 22:30")]
    [InlineData(Days.Mon, "Mondays at 22:30")]
    [InlineData(Days.Mon | Days.Fri, "Mondays and Fridays at 22:30")]
    [InlineData(Days.Mon | Days.Wed | Days.Sun, "Mondays, Wednesdays and Sundays at 22:30")]
    public void AtSentences(Days days, string expected) =>
        Assert.Equal(expected, WhenThisRuns.Sentence(new AtTrigger(new TimeOnly(22, 30), days)));

    [Theory]
    [InlineData(60, "Every hour")]
    [InlineData(120, "Every 2 hours")]
    [InlineData(1, "Every minute")]
    [InlineData(90, "Every 90 minutes")]
    public void EverySentences(int minutes, string expected) =>
        Assert.Equal(expected, WhenThisRuns.Sentence(new EveryTrigger(TimeSpan.FromMinutes(minutes))));

    [Fact]
    public void EveryWithAWindowAndOnceSentences()
    {
        Assert.Equal("Every 2 hours from 09:00 to 18:00",
            WhenThisRuns.Sentence(new EveryTrigger(TimeSpan.FromHours(2), new TimeOnly(9, 0), new TimeOnly(18, 0))));
        Assert.Equal("Once, 1 Dec 2026 at 09:30", WhenThisRuns.Sentence(new OnceTrigger(new DateTime(2026, 12, 1, 9, 30, 0))));
    }

    [Fact]
    public void EventSentencesAndTheOnlyCondition()
    {
        Assert.Equal("When the PC is unlocked", WhenThisRuns.Sentence(new EventTrigger("session.unlock")));
        Assert.Equal("When the PC is unlocked, only on battery", WhenThisRuns.Sentence(new EventTrigger("session.unlock") { Only = "onBattery" }));
        Assert.Equal("When Daze Control starts", WhenThisRuns.Sentence(new EngineStartTrigger()));
    }

    [Fact]
    public void NetworkSentencesSayWhichConnection()
    {
        Assert.Equal("When the network connects", WhenThisRuns.Sentence(new EventTrigger("network.connected")));
        Assert.Equal("When a cable is plugged in", WhenThisRuns.Sentence(new EventTrigger("network.connected") { Via = NetworkVia.Cable }));
        Assert.Equal("When a cable is unplugged", WhenThisRuns.Sentence(new EventTrigger("network.disconnected") { Via = NetworkVia.Cable }));
        Assert.Equal("When Wi-Fi connects", WhenThisRuns.Sentence(new EventTrigger("network.connected") { Via = NetworkVia.WiFi }));
        Assert.Equal("When Wi-Fi 'Home' connects", WhenThisRuns.Sentence(new EventTrigger("network.connected") { Ssid = "Home" }));
        Assert.Equal("When Wi-Fi 'Home' disconnects, only on battery",
            WhenThisRuns.Sentence(new EventTrigger("network.disconnected") { Via = NetworkVia.WiFi, Ssid = "Home", Only = "onBattery" }));
    }

    [Fact]
    public void ConnectionEditsKeepTheFiltersConsistent()
    {
        Trigger t = new EventTrigger("network.connected") { Only = "onAC" };
        t = WhenThisRuns.WithConnection(t, WhenThisRuns.WiFiConnection);
        t = WhenThisRuns.WithNetworkName(t, "  Home ");
        Assert.Equal(new EventTrigger("network.connected") { Only = "onAC", Via = NetworkVia.WiFi, Ssid = "Home" }, t);

        t = WhenThisRuns.WithEvent(t, "network.disconnected"); // the other network event keeps the choice
        Assert.Equal(new EventTrigger("network.disconnected") { Only = "onAC", Via = NetworkVia.WiFi, Ssid = "Home" }, t);

        Assert.Equal(new EventTrigger("network.disconnected") { Only = "onAC", Via = NetworkVia.Cable }, WhenThisRuns.WithConnection(t, WhenThisRuns.CableConnection));
        Assert.Equal(new EventTrigger("network.disconnected") { Only = "onAC" }, WhenThisRuns.WithConnection(t, WhenThisRuns.AnyConnection));
        Assert.Equal(new EventTrigger("network.disconnected") { Only = "onAC", Via = NetworkVia.WiFi }, WhenThisRuns.WithNetworkName(t, " "));
        Assert.Equal(new EventTrigger("session.unlock") { Only = "onAC" }, WhenThisRuns.WithEvent(t, "session.unlock")); // filters don't follow out
        Assert.Equal(WhenThisRuns.WiFiConnection, WhenThisRuns.ConnectionIndex(new EventTrigger("network.connected") { Ssid = "Home" }));

        var unlock = new EventTrigger("session.unlock");
        Assert.Same(unlock, WhenThisRuns.WithConnection(unlock, WhenThisRuns.CableConnection));
        Assert.Same(unlock, WhenThisRuns.WithNetworkName(unlock, "Home"));
    }

    [Fact]
    public void EveryEventInCoreHasASentenceAndIsInThePicker()
    {
        foreach (var name in TriggerText.Events)
        {
            Assert.Contains(WhenThisRuns.EventChoices, c => c.Name == name);
            Assert.StartsWith("When ", WhenThisRuns.Event(name));
        }
        Assert.Equal(TriggerText.Events.Count + 1, WhenThisRuns.EventChoices.Count); // + engine start
    }

    [Fact]
    public void NextLineSaysTodayTomorrowWeekdayOrDate()
    {
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 8)));
        string Text(DateTime local) => WhenThisRuns.NextText(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)), now);
        Assert.Equal("Next: today 22:30", Text(new DateTime(2026, 11, 8, 22, 30, 0)));
        Assert.Equal("Next: tomorrow 09:00", Text(new DateTime(2026, 11, 9, 9, 0, 0)));
        Assert.Equal("Next: Wed 09:00", Text(new DateTime(2026, 11, 11, 9, 0, 0)));
        Assert.Equal("Next: 3 Dec 09:00", Text(new DateTime(2026, 12, 3, 9, 0, 0)));
        Assert.Equal("Next: never", WhenThisRuns.NextText(null, now));
    }

    [Fact]
    public void ANewScheduleChoosesWhatToDoWhenMissedNow()
    {
        foreach (var kind in new[] { WhenThisRuns.AtKind, WhenThisRuns.EveryKind, WhenThisRuns.OnceKind })
        {
            var made = Assert.IsAssignableFrom<ScheduleTrigger>(WhenThisRuns.Create(kind, Now));
            Assert.Equal(IfMissed.Run, made.IfMissed);
            Assert.Equal(TimeSpan.FromMinutes(10), made.Grace);
        }
        Assert.IsType<EventTrigger>(WhenThisRuns.Create(WhenThisRuns.EventKind, Now));
    }

    [Fact]
    public void ChangingTheKindKeepsTheMissedChoiceAndOnlyWhereTheyApply()
    {
        var at = new AtTrigger(new TimeOnly(8, 0), Days.Weekdays) { IfMissed = IfMissed.Skip, Grace = TimeSpan.FromMinutes(3) };
        var every = Assert.IsType<EveryTrigger>(WhenThisRuns.ChangeKind(at, WhenThisRuns.EveryKind, Now));
        Assert.Equal(IfMissed.Skip, every.IfMissed);
        Assert.Equal(TimeSpan.FromMinutes(3), every.Grace);
        var ev = Assert.IsType<EventTrigger>(WhenThisRuns.ChangeKind(every, WhenThisRuns.EventKind, Now));
        Assert.Null(ev.Only);
        var back = Assert.IsType<AtTrigger>(WhenThisRuns.ChangeKind(ev with { Only = "onAC" }, WhenThisRuns.AtKind, Now));
        Assert.Null(back.Only);
        Assert.Same(at, WhenThisRuns.ChangeKind(at, WhenThisRuns.AtKind, Now));
    }

    [Fact]
    public void DaysToggleButTheLastOneStays()
    {
        Trigger t = new AtTrigger(new TimeOnly(8, 0), Days.Mon | Days.Tue);
        t = WhenThisRuns.ToggleDay(t, 2); // Wed on
        Assert.Equal(Days.Mon | Days.Tue | Days.Wed, ((AtTrigger)t).Days);
        t = WhenThisRuns.ToggleDay(WhenThisRuns.ToggleDay(WhenThisRuns.ToggleDay(t, 0), 1), 2);
        Assert.Equal(Days.Wed, ((AtTrigger)t).Days); // the last: refused
        Assert.Equal(Days.Wed, ((AtTrigger)WhenThisRuns.ToggleDay(t, 2)).Days);
    }

    [Fact]
    public void IntervalAndWindowEditsStayValid()
    {
        Trigger t = new EveryTrigger(TimeSpan.FromHours(1));
        Assert.Equal(EveryTrigger.MinInterval, ((EveryTrigger)WhenThisRuns.WithInterval(t, TimeSpan.Zero)).Interval);
        t = WhenThisRuns.WithWindow(t, true);
        Assert.Equal(new TimeOnly(9, 0), ((EveryTrigger)t).From);
        Assert.Equal(new TimeOnly(18, 0), ((EveryTrigger)t).To);
        var same = (EveryTrigger)WhenThisRuns.WithWindowTimes(t, null, new TimeOnly(9, 0));
        Assert.NotEqual(same.From, same.To); // a window of no length would be ignored by the validator
        var off = (EveryTrigger)WhenThisRuns.WithWindow(t, false);
        Assert.Null(off.From);
        Assert.Null(off.To);
    }

    [Fact]
    public void OnlyAppliesToEventsAndGraceToSchedules()
    {
        var ev = WhenThisRuns.WithOnly(new EventTrigger("power.resume"), 1);
        Assert.Equal("onBattery", ev.Only);
        Assert.Equal("onAC", WhenThisRuns.WithOnly(ev, 2).Only);
        Assert.Null(WhenThisRuns.WithOnly(ev, 0).Only);
        var at = new AtTrigger(new TimeOnly(8, 0), Days.Daily);
        Assert.Same(at, WhenThisRuns.WithOnly(at, 1));
        Assert.Equal(TimeSpan.FromMinutes(1440), ((AtTrigger)WhenThisRuns.WithGrace(at, 99999)).Grace);
        Assert.Equal(IfMissed.Skip, ((AtTrigger)WhenThisRuns.WithIfMissed(at, IfMissed.Skip)).IfMissed);
        Assert.Same(ev, WhenThisRuns.WithIfMissed(ev, IfMissed.Skip));
    }

    [Fact]
    public void EventPickerSwitchesBetweenEngineStartAndNamedEvents()
    {
        var start = WhenThisRuns.WithEvent(new EventTrigger("session.lock") { Only = "onAC" }, EngineStartTrigger.Name);
        Assert.IsType<EngineStartTrigger>(start);
        Assert.Equal("onAC", start.Only);
        var back = Assert.IsType<EventTrigger>(WhenThisRuns.WithEvent(start, "display.changed"));
        Assert.Equal("display.changed", back.Name);
        Assert.Equal("onAC", back.Only);
    }

    [Fact]
    public void ChipTipListsEverySentence()
    {
        Trigger[] triggers = [new AtTrigger(new TimeOnly(22, 30), Days.Weekdays), new EventTrigger("session.unlock")];
        Assert.True(WhenThisRuns.HasSchedule(triggers));
        Assert.True(WhenThisRuns.HasEvent(triggers));
        Assert.Equal("Weekdays at 22:30; When the PC is unlocked", WhenThisRuns.ChipTip(triggers));
        Assert.False(WhenThisRuns.HasSchedule([new EventTrigger("session.lock")]));
    }
}
