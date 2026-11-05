using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

public sealed class GamepadTableTests
{
    private const GamepadButtons ViewMenu = GamepadButtons.View | GamepadButtons.Menu;
    private readonly GamepadTable _table = new();
    private int _changes;

    public GamepadTableTests() => _table.ActiveChanged += () => _changes++;

    private static Flow Flow(string id, params string[] chords) => new(id, id, []) { Gamepad = [.. chords] };

    private (string FlowId, GamepadChord Chord)? Tap(GamepadButtons chord)
    {
        _table.Feed(chord);
        return _table.Feed(GamepadButtons.None);
    }

    [Fact]
    public void ChordRunsItsFlowOnRelease()
    {
        _table.Apply([Flow("a", "Menu+View"), Flow("b", "LB+RB+Y")]);

        Assert.True(_table.NeedsSource);
        Assert.Equal("a", Tap(ViewMenu)?.FlowId);
        Assert.Equal("b", Tap(GamepadButtons.LB | GamepadButtons.RB | GamepadButtons.Y)?.FlowId);
        Assert.Null(Tap(GamepadButtons.A | GamepadButtons.B));
    }

    [Fact]
    public void FirstFlowKeepsAClaimedChordAndBadTextIsReported()
    {
        _table.Apply([Flow("a", "View+Menu"), Flow("b", "Menu+View", "Guide+A")]);

        Assert.Equal("a", Tap(ViewMenu)?.FlowId);
        Assert.Equal(["b: Guide+A"], _table.Invalid);
        Assert.Equal(new GamepadChord(ViewMenu), _table.ChordOf("a"));
    }

    [Fact]
    public void NoChordsMeansNoSource()
    {
        _table.Apply([Flow("a")]);
        Assert.False(_table.NeedsSource);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void ReloadIsADiff()
    {
        _table.Apply([Flow("a", "View+Menu")]);
        Assert.Equal(1, _changes);
        _table.Apply([Flow("a", "View+Menu"), Flow("b")]); // same chords: nothing to do
        Assert.Equal(1, _changes);
        _table.Apply([Flow("a", "View+Menu"), Flow("b", "A+B")]);
        Assert.Equal(2, _changes);
        _table.Apply([Flow("b", "A+B")]);
        Assert.Equal(3, _changes);
        Assert.Null(Tap(ViewMenu));
        Assert.Equal("b", Tap(GamepadButtons.A | GamepadButtons.B)?.FlowId);
        _table.Apply([]);
        Assert.False(_table.NeedsSource);
    }

    [Fact]
    public void ReloadKeepsAPressInProgress()
    {
        _table.Apply([Flow("a", "View+Menu")]);
        _table.Feed(ViewMenu);
        _table.Apply([Flow("a", "View+Menu"), Flow("b", "A+B")]);
        Assert.Equal("a", _table.Feed(GamepadButtons.None)?.FlowId);
    }

    [Fact]
    public void PauseSilencesChordsUntilResume()
    {
        _table.Apply([Flow("a", "View+Menu")]);
        _table.Pause();
        Assert.False(_table.NeedsSource);
        Assert.Null(Tap(ViewMenu));
        _table.Apply([Flow("a", "View+Menu")]); // a reload while paused stays quiet
        Assert.False(_table.NeedsSource);
        _table.Resume();
        Assert.True(_table.NeedsSource);
        Assert.Equal("a", Tap(ViewMenu)?.FlowId);
    }

    [Fact]
    public void PauseDuringAPressDoesNotFireAfterwards()
    {
        _table.Apply([Flow("a", "View+Menu")]);
        _table.Feed(ViewMenu);
        _table.Pause();
        _table.Resume();
        Assert.Null(_table.Feed(GamepadButtons.None));
    }

    [Fact]
    public void RecordingSilencesChordsAndIsIndependentOfPause()
    {
        _table.Apply([Flow("a", "View+Menu")]);
        _table.StartRecording();
        Assert.False(_table.NeedsSource);
        Assert.Null(Tap(ViewMenu));
        _table.Pause();
        _table.StopRecording();
        Assert.False(_table.NeedsSource); // still paused
        _table.Resume();
        Assert.Equal("a", Tap(ViewMenu)?.FlowId);
    }
}
