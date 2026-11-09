using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>M20 T20.6: every row operation of the "When this runs" card and the ask inputs, through the draft (D-047).</summary>
public sealed class InspectorTriggersTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
        flows:
          night:
            name: Night
            hotkeys: [Alt+N]
            triggers:
              - { at: "22:30", days: weekdays }
              - { on: session.unlock }
            nodes:
              - { id: n, step: notify, with: { text: x } }
          plain:
            name: Plain
            inputs:
              out: { kind: audio, name: Output, default: technics }
            nodes:
              - { id: n, step: notify, with: { text: x } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private FlowCanvasViewModel _canvas = null!;
    private InspectorViewModel _inspector = null!;

    public InspectorTriggersTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        _engine.Answer = _ => new PipeMessage { Ok = true };
        Open("night");
    }

    private void Open(string flowId)
    {
        _inspector?.Dispose();
        _canvas?.Dispose();
        _canvas = new FlowCanvasViewModel(_session, flowId);
        _inspector = new InspectorViewModel(_session, _canvas, _engine, action => action(), _ => true, time: _time);
    }

    public void Dispose()
    {
        _inspector.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private ValueList<Trigger> Saved => _canvas.Flow!.Triggers;

    [Fact]
    public void RowsShowTheSentencesAndTheNextRun()
    {
        Assert.Equal(["Weekdays at 22:30", "When the PC is unlocked"], _inspector.Triggers.Select(r => r.Sentence));
        Assert.True(_inspector.Triggers[0].IsSchedule);
        Assert.StartsWith("Next: ", _inspector.Triggers[0].Next);
        Assert.False(_inspector.Triggers[1].HasNext);
        Assert.Equal(["Alt+N"], _inspector.Shortcuts.Select(s => s.Hotkey)); // the hotkey rows stay beside them
    }

    [Fact]
    public void AddScheduleAppendsOpenWithTheMissedChoiceMade()
    {
        _inspector.AddScheduleCommand.Execute(null);

        Assert.Equal(3, _inspector.Triggers.Count);
        var added = Assert.IsType<AtTrigger>(Saved[2]);
        Assert.Equal(IfMissed.Run, added.IfMissed);
        Assert.Equal(TimeSpan.FromMinutes(10), added.Grace);
        Assert.True(_inspector.Triggers[2].IsOpen);
        Assert.False(_inspector.Triggers[0].IsOpen);
    }

    [Fact]
    public void AddEventAppendsAnUnlockEvent()
    {
        _inspector.AddEventCommand.Execute(null);

        Assert.Equal(new EventTrigger("session.unlock"), Saved[^1]);
    }

    [Fact]
    public void RemoveDropsOnlyThatRow()
    {
        _inspector.Triggers[0].RemoveCommand.Execute(null);

        Assert.Equal([new EventTrigger("session.unlock")], Saved);
        Assert.Single(_inspector.Triggers);
    }

    [Fact]
    public void TimeAndDayPickersEditTheSchedule()
    {
        _inspector.Triggers[0].Time = new TimeSpan(7, 5, 0);
        Assert.Equal(new TimeOnly(7, 5), Assert.IsType<AtTrigger>(Saved[0]).Time);

        _inspector.Triggers[0].DayToggles[5].IsOn = true; // Sat
        Assert.Equal(Days.Weekdays | Days.Sat, Assert.IsType<AtTrigger>(Saved[0]).Days);
        Assert.Equal("Mondays, Tuesdays, Wednesdays, Thursdays, Fridays and Saturdays at 07:05", _inspector.Triggers[0].Sentence);
    }

    [Fact]
    public void SwitchingTheLastDayOffIsRefusedAndShownAgain()
    {
        _inspector.Triggers[0].DayToggles[0].IsOn = false;
        _inspector.Triggers[0].DayToggles[1].IsOn = false;
        _inspector.Triggers[0].DayToggles[2].IsOn = false;
        _inspector.Triggers[0].DayToggles[3].IsOn = false;
        _inspector.Triggers[0].DayToggles[4].IsOn = false;

        Assert.Equal(Days.Fri, Assert.IsType<AtTrigger>(Saved[0]).Days);
        Assert.True(_inspector.Triggers[0].DayToggles[4].IsOn);
    }

    [Fact]
    public void KindPickerTurnsAScheduleIntoAnIntervalAndBack()
    {
        _inspector.Triggers[0].KindIndex = WhenThisRuns.EveryKind;
        var every = Assert.IsType<EveryTrigger>(Saved[0]);
        Assert.Equal(TimeSpan.FromHours(1), every.Interval);

        _inspector.Triggers[0].IntervalCount = 2;
        _inspector.Triggers[0].HasWindow = true;
        _inspector.Triggers[0].To = new TimeSpan(20, 0, 0);
        Assert.Equal(new EveryTrigger(TimeSpan.FromHours(2), new TimeOnly(9, 0), new TimeOnly(20, 0)), Saved[0]);
        Assert.Equal("Every 2 hours from 09:00 to 20:00", _inspector.Triggers[0].Sentence);

        _inspector.Triggers[0].IntervalUnitIndex = 0;
        Assert.Equal(TimeSpan.FromMinutes(2), Assert.IsType<EveryTrigger>(Saved[0]).Interval);
    }

    [Fact]
    public void OncePickersEditDateAndTime()
    {
        _inspector.Triggers[0].KindIndex = WhenThisRuns.OnceKind;
        var date = new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 12, 24)));
        _inspector.Triggers[0].OnceDate = date;
        _inspector.Triggers[0].Time = new TimeSpan(18, 15, 0);

        Assert.Equal(new DateTime(2026, 12, 24, 18, 15, 0), Assert.IsType<OnceTrigger>(Saved[0]).When);
    }

    [Fact]
    public void EventAndOnlyPickersEditTheEvent()
    {
        var row = _inspector.Triggers[1];
        row.EventIndex = WhenThisRuns.EventChoices.ToList().FindIndex(c => c.Name == "power.resume");
        row.OnlyIndex = 1;

        Assert.Equal(new EventTrigger("power.resume") { Only = "onBattery" }, Saved[1]);
        Assert.Equal("When the PC wakes up, only on battery", _inspector.Triggers[1].Sentence);
    }

    [Fact]
    public void MissedChoiceAndGraceEditTheSchedule()
    {
        var row = _inspector.Triggers[0];
        Assert.True(row.ShowGrace);
        row.GraceMinutes = 30;
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.IsType<AtTrigger>(Saved[0]).Grace);

        row.IfMissedIndex = 1;
        Assert.Equal(IfMissed.Skip, Assert.IsType<AtTrigger>(Saved[0]).IfMissed);
        Assert.False(_inspector.Triggers[0].ShowGrace);
    }

    [Fact]
    public void EditsAreDraftChangesThatUndo()
    {
        _inspector.Triggers[0].Time = new TimeSpan(7, 5, 0);
        Assert.True(_canvas.CanUndo);

        _canvas.Undo();

        Assert.Equal(new TimeOnly(22, 30), Assert.IsType<AtTrigger>(Saved[0]).Time);
    }

    [Fact]
    public async Task TestThisTriggerRunsTheFlowNamedAsTheTriggerWould()
    {
        await _inspector.TestTriggerAsync(1);

        var request = Assert.Single(_engine.Requests, r => r.Op == Ops.Run);
        Assert.Equal("night", request.Flow);
        Assert.Equal("Event · PC unlocked", request.Trigger);
        Assert.StartsWith("Started", _inspector.Triggers[1].TestResult);
    }

    [Fact]
    public async Task TestThisTriggerSaysWhenTheEngineIsOff()
    {
        _engine.SetRunning(false);

        await _inspector.TestTriggerAsync(0);

        Assert.Equal("The engine isn't running.", _inspector.Triggers[0].TestResult);
    }

    [Fact]
    public void ASchedulesOnlyIsNotShownBecauseTheRuntimeNeverChecksIt() =>
        Assert.Equal("Weekdays at 22:30", WhenThisRuns.Sentence(new AtTrigger(new TimeOnly(22, 30), Days.Weekdays) { Only = "onBattery" }));

    [Fact]
    public void IntervalsStopAt24HoursBecauseTheyRestartAtMidnight()
    {
        _inspector.Triggers[0].KindIndex = WhenThisRuns.EveryKind;
        _inspector.Triggers[0].IntervalUnitIndex = 1;
        Assert.Equal(24, _inspector.Triggers[0].IntervalMax);
        _inspector.Triggers[0].IntervalCount = 99;
        Assert.Equal(TimeSpan.FromHours(24), Assert.IsType<EveryTrigger>(Saved[0]).Interval);
        _inspector.Triggers[0].IntervalUnitIndex = 0;
        Assert.Equal(1440, _inspector.Triggers[0].IntervalMax);
    }

    [Fact]
    public async Task TestResultLandsOnTheSameTriggerEvenWhenARowWasRemovedMeanwhile()
    {
        _engine.Answer = _ =>
        {
            _inspector.RemoveTrigger(0); // while the engine answers
            return new PipeMessage { Ok = true };
        };

        await _inspector.TestTriggerAsync(1);

        var row = Assert.Single(_inspector.Triggers);
        Assert.Equal("When the PC is unlocked", row.Sentence);
        Assert.StartsWith("Started", row.TestResult);
    }

    [Fact]
    public async Task TestResultOfTheSecondOfTwoEqualTriggersShowsOnTheSecond()
    {
        _inspector.AddEventCommand.Execute(null); // a second session.unlock beside the first
        Assert.Equal(Saved[1], Saved[2]);

        await _inspector.TestTriggerAsync(2);

        Assert.Null(_inspector.Triggers[1].TestResult);
        Assert.StartsWith("Started", _inspector.Triggers[2].TestResult);
    }

    [Fact]
    public void AnIgnoredTriggerIsSaidUnderTheRowsNotOnAWrongRow()
    {
        File.WriteAllText(_session.Path, Yaml.Replace("{ at: \"22:30\", days: weekdays }", "{ at: \"25:00\" }" + Environment.NewLine + "      - { at: \"22:30\", days: weekdays }", StringComparison.Ordinal));
        _session.Load();
        Open("night");

        Assert.Equal(2, _inspector.Triggers.Count);
        Assert.True(_inspector.HasTriggerNotes);
        Assert.Contains("ignored", _inspector.TriggerNotes, StringComparison.Ordinal);
    }

    // ---- Ask inputs ----

    [Fact]
    public void AskToggleTimeoutAndNoAnswerEditTheInput()
    {
        Open("plain");
        var row = _inspector.Inputs[0];
        Assert.False(row.Ask);

        row.Ask = true;
        Assert.True(_canvas.Flow!.Inputs["out"].Ask);

        row.TimeoutSeconds = 90;
        Assert.Equal(TimeSpan.FromSeconds(90), _canvas.Flow.Inputs["out"].Timeout);

        row.TimeoutSeconds = 1; // below 5 s
        Assert.Equal(TimeSpan.FromSeconds(5), _canvas.Flow.Inputs["out"].Timeout);

        row.OnTimeoutIndex = 2;
        Assert.Equal(Prompts.UseDefault, _canvas.Flow.Inputs["out"].OnTimeout);
        row.OnTimeoutIndex = 1;
        Assert.Equal(Prompts.Stop, _canvas.Flow.Inputs["out"].OnTimeout);
        row.OnTimeoutIndex = 0;
        Assert.Equal("", _canvas.Flow.Inputs["out"].OnTimeout);

        row.Ask = false;
        Assert.False(_canvas.Flow.Inputs["out"].Ask);
    }
}
