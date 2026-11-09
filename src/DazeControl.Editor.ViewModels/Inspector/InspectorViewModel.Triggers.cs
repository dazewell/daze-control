using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Model;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Inspector;

// "When this runs" (M20 T20.6, DESIGN §5): the Schedule and Event rows beside the Shortcut rows. Edits go through the
// draft like every flow setting (D-047); the rows are rebuilt from the flow on each refresh.
public sealed partial class InspectorViewModel
{
    public ObservableCollection<TriggerRowViewModel> Triggers { get; } = [];

    /// <summary>The "When this runs" card is empty of schedules and events: the hint under the add links shows.</summary>
    public const string AddScheduleHelp = "Run this flow at a time, or when something happens on this PC.";

    private int _openTrigger = -1;

    /// <summary>The editor's clock: "Next:" lines and the date a new schedule starts from.</summary>
    internal TimeProvider Clock => _time;

    private void LoadTriggers(Flow flow)
    {
        var now = _time.GetLocalNow();
        var problems = _session.Problems.Where(p => p.Path.StartsWith($"flows.{flow.Id}.triggers[", StringComparison.Ordinal)).ToList();
        var open = Triggers.Where(r => r.IsOpen).Select(r => r.Index).ToHashSet();
        Triggers.Clear();
        for (var i = 0; i < flow.Triggers.Count; i++)
        {
            var trigger = flow.Triggers[i];
            var row = new TriggerRowViewModel(this, i, trigger, now, _openTrigger == i || open.Contains(i))
            {
                Problem = problems.FirstOrDefault(p => p.Path.StartsWith($"flows.{flow.Id}.triggers[{i}]", StringComparison.Ordinal))?.Message,
            };
            Triggers.Add(row);
        }
        _openTrigger = -1;
    }

    /// <summary>"Add schedule" / "Add event": a new row of the kind, open so its missed-run choice is seen when it is created.</summary>
    public void AddTrigger(int kind)
    {
        if (Flow is not { } flow || !CanEdit)
            return;
        var made = WhenThisRuns.Create(kind, _time.GetLocalNow());
        _openTrigger = flow.Triggers.Count;
        EditFlow(f => f with { Triggers = [.. f.Triggers, made] });
    }

    [RelayCommand]
    private void AddSchedule() => AddTrigger(WhenThisRuns.AtKind);

    [RelayCommand]
    private void AddEvent() => AddTrigger(WhenThisRuns.EventKind);

    public void RemoveTrigger(int index)
    {
        if (Flow is { } flow && index >= 0 && index < flow.Triggers.Count)
            EditFlow(f => f with { Triggers = [.. f.Triggers.Where((_, i) => i != index)] });
    }

    /// <summary>Replaces row <paramref name="index"/> by <paramref name="change"/> of it (unchanged = no draft edit).</summary>
    internal void EditTrigger(int index, Func<Trigger, Trigger> change)
    {
        if (Flow is not { } flow || index < 0 || index >= flow.Triggers.Count)
            return;
        var before = flow.Triggers[index];
        var after = change(before);
        if (after.Equals(before))
        {
            LoadTriggers(flow); // a refused value (the last day, a zero interval): the row shows what is real again
            return;
        }
        _openTrigger = index;
        EditFlow(f => f with { Triggers = [.. f.Triggers.Select((t, i) => i == index ? after : t)] });
    }

    /// <summary>"Test this trigger": runs the saved flow now, named as the trigger would start it, so its prompts behave the same.</summary>
    internal async Task TestTriggerAsync(int index)
    {
        if (Flow is not { } flow || index < 0 || index >= flow.Triggers.Count)
            return;
        var trigger = flow.Triggers[index];
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Run, Flow = flow.Id, Trigger = DazeControl.Core.Model.TriggerText.Describe(trigger) }).ConfigureAwait(false);
        var text = answer is { Ok: true } ? "Started. See Runs for how it went." : answer?.Error ?? "The engine isn't running.";
        _post(() =>
        {
            if (index < Triggers.Count)
                Triggers[index].TestResult = text;
        });
    }
}

/// <summary>One Schedule or Event row of the card: its sentence, next run, and the pickers of its kind.</summary>
public sealed partial class TriggerRowViewModel : ObservableObject
{
    private readonly InspectorViewModel _owner;
    private bool _loading;

    internal TriggerRowViewModel(InspectorViewModel owner, int index, Trigger trigger, DateTimeOffset now, bool open)
    {
        _owner = owner;
        Index = index;
        IsOpen = open;
        _loading = true;
        Trigger = trigger;
        Sentence = WhenThisRuns.Sentence(trigger);
        KindIndex = WhenThisRuns.KindOf(trigger);
        OnlyIndex = WhenThisRuns.OnlyIndex(trigger);
        switch (trigger)
        {
            case AtTrigger at:
                Time = at.Time.ToTimeSpan();
                foreach (var (name, i) in WhenThisRuns.DayNames.Select((n, i) => (n, i)))
                    DayToggles.Add(new DayToggleViewModel(this, i, name, at.Days.HasFlag((Days)(1 << i))));
                break;
            case EveryTrigger every:
                var hours = every.Interval.Ticks % TimeSpan.TicksPerHour == 0;
                IntervalUnitIndex = hours ? 1 : 0;
                IntervalCount = hours ? every.Interval.TotalHours : Math.Round(every.Interval.TotalMinutes);
                HasWindow = every.From is not null;
                From = (every.From ?? new TimeOnly(9, 0)).ToTimeSpan();
                To = (every.To ?? new TimeOnly(18, 0)).ToTimeSpan();
                break;
            case OnceTrigger once:
                OnceDate = new DateTimeOffset(once.When.Date, TimeZoneInfo.Local.GetUtcOffset(once.When.Date));
                Time = once.When.TimeOfDay;
                break;
            case EventTrigger e:
                EventIndex = Math.Max(0, IndexOfEvent(e.Name));
                break;
            case EngineStartTrigger:
                EventIndex = 0;
                break;
        }
        if (trigger is ScheduleTrigger schedule)
        {
            IfMissedIndex = (int)schedule.IfMissed;
            GraceMinutes = (schedule.Grace ?? TimeSpan.FromMinutes(WhenThisRuns.GraceDefaultMinutes)).TotalMinutes;
            Next = WhenThisRuns.NextText(ScheduleTable.Next(schedule, now, _owner.Clock), now);
        }
        _loading = false;
    }

    private static int IndexOfEvent(string name) => WhenThisRuns.EventChoices.ToList().FindIndex(c => c.Name == name);

    public int Index { get; }

    internal Trigger Trigger { get; }

    public string Sentence { get; }

    public static IReadOnlyList<string> KindNames => WhenThisRuns.Kinds;

    public static IReadOnlyList<string> OnlyNames => WhenThisRuns.OnlyNames;

    public static IReadOnlyList<string> EventNames { get; } = [.. WhenThisRuns.EventChoices.Select(c => c.Sentence)];

    public static IReadOnlyList<string> UnitNames { get; } = ["minutes", "hours"];

    public static IReadOnlyList<string> IfMissedNames { get; } = ["Run it late", "Skip it"];

    public ObservableCollection<DayToggleViewModel> DayToggles { get; } = [];

    public bool IsSchedule => Trigger is ScheduleTrigger;

    public bool IsAt => Trigger is AtTrigger;

    public bool IsEvery => Trigger is EveryTrigger;

    public bool IsOnce => Trigger is OnceTrigger;

    public bool IsEvent => !IsSchedule;

    /// <summary>Time of day for "At a time" and "Once".</summary>
    public bool HasTime => Trigger is AtTrigger or OnceTrigger;

    /// <summary>Run late only: how late is still worth running.</summary>
    public bool ShowGrace => IsSchedule && IfMissedIndex == (int)IfMissed.Run;

    public string Kicker => IsSchedule ? "Schedule" : "Event";

    public string Glyph => IsSchedule ? "" : ""; // clock, lightning bolt

    public string RemoveName => $"Remove {Sentence}";

    public string TestName => $"Test: {Sentence}";

    public string? Next { get; }

    public bool HasNext => Next is not null;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; internal set; }

    public bool HasProblem => Problem is not null;

    /// <summary>What "Test this trigger" said: started, or why not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; internal set; }

    public bool HasTestResult => TestResult is not null;

    [ObservableProperty]
    public partial int KindIndex { get; set; }

    [ObservableProperty]
    public partial TimeSpan Time { get; set; }

    [ObservableProperty]
    public partial TimeSpan From { get; set; }

    [ObservableProperty]
    public partial TimeSpan To { get; set; }

    [ObservableProperty]
    public partial bool HasWindow { get; set; }

    [ObservableProperty]
    public partial double IntervalCount { get; set; } = 1;

    [ObservableProperty]
    public partial int IntervalUnitIndex { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset OnceDate { get; set; }

    [ObservableProperty]
    public partial int EventIndex { get; set; }

    [ObservableProperty]
    public partial int OnlyIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrace))]
    public partial int IfMissedIndex { get; set; }

    [ObservableProperty]
    public partial double GraceMinutes { get; set; }

    private void Edit(Func<Trigger, Trigger> change)
    {
        if (!_loading)
            _owner.EditTrigger(Index, change);
    }

    partial void OnKindIndexChanged(int value) => Edit(t => WhenThisRuns.ChangeKind(t, value, _owner.Clock.GetLocalNow()));

    partial void OnTimeChanged(TimeSpan value) => Edit(t => t is OnceTrigger once
        ? WhenThisRuns.WithWhen(t, once.When.Date + value)
        : WhenThisRuns.WithTime(t, TimeOnly.FromTimeSpan(value)));

    partial void OnOnceDateChanged(DateTimeOffset value) => Edit(t => t is OnceTrigger once ? WhenThisRuns.WithWhen(t, value.Date + once.When.TimeOfDay) : t);

    partial void OnHasWindowChanged(bool value) => Edit(t => WhenThisRuns.WithWindow(t, value));

    partial void OnFromChanged(TimeSpan value) => Edit(t => WhenThisRuns.WithWindowTimes(t, TimeOnly.FromTimeSpan(value), null));

    partial void OnToChanged(TimeSpan value) => Edit(t => WhenThisRuns.WithWindowTimes(t, null, TimeOnly.FromTimeSpan(value)));

    partial void OnIntervalCountChanged(double value) => Edit(t => WhenThisRuns.WithInterval(t, Interval(value, IntervalUnitIndex)));

    partial void OnIntervalUnitIndexChanged(int value) => Edit(t => WhenThisRuns.WithInterval(t, Interval(IntervalCount, value)));

    private static TimeSpan Interval(double count, int unit)
    {
        var n = double.IsNaN(count) ? 1 : Math.Clamp(Math.Round(count), 1, 1440);
        return unit == 1 ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
    }

    partial void OnEventIndexChanged(int value) => Edit(t => value >= 0 && value < WhenThisRuns.EventChoices.Count ? WhenThisRuns.WithEvent(t, WhenThisRuns.EventChoices[value].Name) : t);

    partial void OnOnlyIndexChanged(int value) => Edit(t => WhenThisRuns.WithOnly(t, value));

    partial void OnIfMissedIndexChanged(int value) => Edit(t => WhenThisRuns.WithIfMissed(t, value == 1 ? IfMissed.Skip : IfMissed.Run));

    partial void OnGraceMinutesChanged(double value) => Edit(t => WhenThisRuns.WithGrace(t, double.IsNaN(value) ? WhenThisRuns.GraceDefaultMinutes : (int)Math.Round(value)));

    internal void ToggleDay(int dayIndex) => Edit(t => WhenThisRuns.ToggleDay(t, dayIndex));

    [RelayCommand]
    private void Remove() => _owner.RemoveTrigger(Index);

    [RelayCommand]
    private Task TestAsync() => _owner.TestTriggerAsync(Index);
}

/// <summary>A weekday switch of an "At a time" row.</summary>
public sealed partial class DayToggleViewModel : ObservableObject
{
    private readonly TriggerRowViewModel _row;
    private readonly int _index;

    internal DayToggleViewModel(TriggerRowViewModel row, int index, string name, bool on)
    {
        _row = row;
        _index = index;
        Name = name;
        IsOn = on;
    }

    public string Name { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        // The toggle already shows the click; a refused change (the last day) is put back by the row's reload.
        _row.ToggleDay(_index);
    }
}
