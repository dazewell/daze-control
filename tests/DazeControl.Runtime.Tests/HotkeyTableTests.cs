using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

public sealed class HotkeyTableTests
{
    private readonly FakeRegistrar _os = new();
    private readonly HotkeyTable _table;

    public HotkeyTableTests() => _table = new HotkeyTable(_os);

    private static Flow Flow(string id, params string[] hotkeys) => new(id, id, []) { Hotkeys = [.. hotkeys] };

    private static Hotkey Key(string text) => Hotkey.TryParse(text, out var hotkey) ? hotkey : throw new ArgumentException(text);

    private (string FlowId, Hotkey Hotkey)? Press(string text) =>
        _os.Registered.TryGetValue(Key(text), out var id) ? _table.Resolve(id) : null;

    [Fact]
    public void RegistersEveryChordOfEveryFlow()
    {
        _table.Apply([Flow("a", "Alt+Z", "Ctrl+Alt+1"), Flow("b", "Win+F5")]);

        Assert.Equal(3, _os.Registered.Count);
        Assert.Equal("a", Press("Ctrl+Alt+1")?.FlowId);
        Assert.Equal("b", Press("Win+F5")?.FlowId);
    }

    [Fact]
    public void UnchangedChordsStayRegisteredAcrossReloads()
    {
        _table.Apply([Flow("a", "Alt+Z"), Flow("b", "Alt+X")]);
        var id = _os.Registered[Key("Alt+Z")];

        _table.Apply([Flow("a", "Alt+Z")]);

        Assert.Equal(id, _os.Registered[Key("Alt+Z")]);
        Assert.False(_os.Registered.ContainsKey(Key("Alt+X")));
        Assert.Equal(3, _os.Calls); // two registrations, then only Alt+X's unregistration
    }

    [Fact]
    public void AChordMovedToAnotherFlowRunsThatFlow()
    {
        _table.Apply([Flow("a", "Alt+Z")]);

        _table.Apply([Flow("b", "Alt+Z")]);

        Assert.Equal("b", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AChordAnotherAppOwnsFailsAloneAndIsRetriedOnTheNextChange()
    {
        _os.Taken.Add(Key("Alt+Z"));

        _table.Apply([Flow("a", "Alt+Z", "Alt+X")]);

        Assert.Equal("taken", _table.Failures[Key("Alt+Z")]);
        Assert.Equal("a", Press("Alt+X")?.FlowId);

        _os.Taken.Clear();
        _table.Apply([Flow("a", "Alt+Z", "Alt+X")]);

        Assert.Empty(_table.Failures);
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AFailureForAChordNoLongerWantedIsForgotten()
    {
        _os.Taken.Add(Key("Alt+Z"));
        _table.Apply([Flow("a", "Alt+Z")]);

        _table.Apply([]);

        Assert.Empty(_table.Failures);
    }

    [Fact]
    public void TheFirstFlowKeepsAClashingChord()
    {
        _table.Apply([Flow("a", "Alt+Z"), Flow("b", "Alt+Z")]);

        Assert.Single(_os.Registered);
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void UnparsableTextIsListedAndSkipped()
    {
        _table.Apply([Flow("a", "Z", "Alt+Z")]);

        Assert.Equal(["a: Z"], _table.Invalid);
        Assert.Single(_os.Registered);
    }

    [Fact]
    public void PauseUnregistersEverythingAndResumeRestoresIt()
    {
        _table.Apply([Flow("a", "Alt+Z")]);

        _table.Pause();
        Assert.Empty(_os.Registered);
        _table.Apply([Flow("a", "Alt+Z"), Flow("b", "Alt+X")]);
        Assert.Empty(_os.Registered);

        _table.Resume();
        Assert.Equal("b", Press("Alt+X")?.FlowId);
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AnOldIdResolvesToNothing()
    {
        _table.Apply([Flow("a", "Alt+Z")]);
        var id = _os.Registered[Key("Alt+Z")];

        _table.Apply([]);

        Assert.Null(_table.Resolve(id));
    }

    [Fact]
    public void TheShortcutShownPrefersARegisteredChord()
    {
        _os.Taken.Add(Key("Alt+Z"));

        _table.Apply([Flow("a", "Alt+Z", "Alt+X"), Flow("b", "Alt+Q")]);

        Assert.Equal(Key("Alt+X"), _table.ShortcutOf("a"));
        Assert.Null(_table.ShortcutOf("c"));
    }

    [Fact]
    public void ClearUnregistersEverything()
    {
        _table.Apply([Flow("a", "Alt+Z")]);

        _table.Clear();

        Assert.Empty(_os.Registered);
    }

    [Fact]
    public void RecordingUnregistersEveryChordAndEndingItRegistersThemAgain()
    {
        _table.Apply([Flow("a", "Alt+Z", "Win+F5")]);

        _table.StartRecording();
        Assert.Empty(_os.Registered);
        Assert.Null(Press("Alt+Z"));

        _table.StopRecording();
        Assert.Equal(2, _os.Registered.Count);
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AReloadWhileRecordingRegistersNothingUntilItEnds()
    {
        _table.StartRecording();
        _table.Apply([Flow("a", "Alt+Z")]);
        Assert.Empty(_os.Registered);

        _table.StopRecording();
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AChordThatFailedAndIsFreeNowIsRegisteredWhenRecordingEnds()
    {
        _os.Taken.Add(Key("Alt+Z"));
        _table.Apply([Flow("a", "Alt+Z")]);
        _table.StartRecording();
        _os.Taken.Clear();

        _table.StopRecording();

        Assert.Empty(_table.Failures);
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AChordThatFailedAndIsFreeNowIsRegisteredOnResume()
    {
        _os.Taken.Add(Key("Alt+Z"));
        _table.Apply([Flow("a", "Alt+Z")]);
        _table.Pause();
        _os.Taken.Clear();

        _table.Resume();

        Assert.Empty(_table.Failures);
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AChordThatFailedAndIsFreeNowIsRegisteredWhenTheMenuOpens()
    {
        _os.Taken.Add(Key("Alt+Z"));
        _table.Apply([Flow("a", "Alt+Z", "Alt+X")]);
        var changes = 0;
        _table.FailuresChanged += () => changes++;
        _os.Taken.Clear();

        _table.RetryFailures();

        Assert.Empty(_table.Failures);
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ARetryThatChangesNothingTouchesOnlyTheFailedChordsAndRaisesNothing()
    {
        _os.Taken.Add(Key("Alt+Z"));
        _table.Apply([Flow("a", "Alt+Z", "Alt+X")]);
        var calls = _os.Calls;
        var changes = 0;
        _table.FailuresChanged += () => changes++;

        _table.RetryFailures();

        Assert.Equal(calls + 1, _os.Calls); // one try for Alt+Z, Alt+X untouched
        Assert.Equal("taken", _table.Failures[Key("Alt+Z")]);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void FailuresAreKeptWhileRecordingSoTheNoteStays()
    {
        _os.Taken.Add(Key("Alt+Z"));
        _table.Apply([Flow("a", "Alt+Z")]);

        _table.StartRecording();
        _table.RetryFailures();

        Assert.Equal("taken", _table.Failures[Key("Alt+Z")]);
        Assert.Empty(_os.Registered);
    }

    [Fact]
    public void RecordingAndPauseAreSeparate()
    {
        _table.Apply([Flow("a", "Alt+Z")]);

        _table.Pause();
        _table.StartRecording();
        _table.StopRecording();
        Assert.Empty(_os.Registered);
        Assert.True(_table.Paused);

        _table.StartRecording();
        _table.Resume();
        Assert.Empty(_os.Registered);
        Assert.False(_table.Paused);

        _table.StopRecording();
        Assert.Equal("a", Press("Alt+Z")?.FlowId);
    }

    [Fact]
    public void AFailureThatGoesAwayOrComesBackIsAChange()
    {
        var changes = 0;
        _table.FailuresChanged += () => changes++;
        _os.Taken.Add(Key("Alt+Z"));

        _table.Apply([Flow("a", "Alt+Z")]);
        Assert.Equal(1, changes);

        _table.Apply([Flow("a", "Alt+Z")]);
        Assert.Equal(1, changes);

        _table.Apply([]);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData("Win+E", "Windows uses Win+E. Try another one.")]
    [InlineData("Win+[", "Another app uses Win+[. If you closed it, check the tray: it may still be running. Try another one.")]
    [InlineData("Ctrl+Alt+Z", "Another app uses Ctrl+Alt+Z. If you closed it, check the tray: it may still be running. Try another one.")]
    public void ATakenChordNamesWindowsWhenItIsWindows(string text, string message) =>
        Assert.Equal(message, HotkeyTable.TakenMessage(Key(text)));

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public Dictionary<Hotkey, int> Registered { get; } = [];

        public HashSet<Hotkey> Taken { get; } = [];

        public int Calls { get; private set; }

        public string? Register(int id, Hotkey hotkey)
        {
            Calls++;
            if (Taken.Contains(hotkey))
                return "taken";
            Assert.True(Registered.TryAdd(hotkey, id));
            return null;
        }

        public void Unregister(int id)
        {
            Calls++;
            var hotkey = Registered.Single(r => r.Value == id).Key;
            Registered.Remove(hotkey);
        }
    }
}
