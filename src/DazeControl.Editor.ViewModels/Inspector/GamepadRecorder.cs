using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Input;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Inspector;

public enum GamepadRecordStatus
{
    /// <summary>A chord came back (<see cref="GamepadRecording.Chord"/>).</summary>
    Chord,
    NoController,
    TimedOut,

    /// <summary>No engine answered (#43).</summary>
    EngineOff,
    Failed,
}

public sealed record GamepadRecording(GamepadRecordStatus Status, string? Chord = null, string? Error = null);

/// <summary>
/// Records the next controller chord through the engine, so what is recorded is what the engine sees (DESIGN §3.6).
/// Cancelling must tell the engine to stop listening.
/// </summary>
public interface IGamepadRecorder
{
    Task<GamepadRecording> RecordAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <c>record-gamepad</c> over the pipe. The op is T12.6's (Ipc isn't touched here), so this codes to DESIGN §3.6: the
/// answer is Ok with the canonical chord in <see cref="PipeMessage.Yaml"/>, or an error that says "no controller" or
/// "timed out". Whoever builds the op adjusts <see cref="Interpret"/> if it answers differently.
/// </summary>
public sealed class EngineGamepadRecorder(IEngineClient engine) : IGamepadRecorder
{
    public const string Op = "record-gamepad";

    public async Task<GamepadRecording> RecordAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Interpret(await engine.SendAsync(new PipeRequest { Op = Op }, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return new(GamepadRecordStatus.Failed, Error: "Stopped.");
        }
    }

    public static GamepadRecording Interpret(PipeMessage? answer)
    {
        if (answer is null)
            return new(GamepadRecordStatus.EngineOff);
        if (answer is { Ok: true, Yaml: { Length: > 0 } chord })
            return new(GamepadRecordStatus.Chord, chord.Trim());
        var error = answer.Error ?? "";
        if (error.Contains("unknown request", StringComparison.OrdinalIgnoreCase))
            return new(GamepadRecordStatus.Failed, Error: GamepadTexts.OldEngine);
        return error.Contains("no controller", StringComparison.OrdinalIgnoreCase) ? new(GamepadRecordStatus.NoController)
            : error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? new(GamepadRecordStatus.TimedOut)
            : new(GamepadRecordStatus.Failed, Error: error);
    }
}

/// <summary>UX §5.7 #34–#37, #43.</summary>
public static class GamepadTexts
{
    public const string Listening = "Press the buttons together on your controller, then let go.";
    public const string NoController = "Daze Control doesn't see a controller. Turn one on and try again.";
    public const string TooFew = "Use at least two buttons, so a game doesn't start it by accident.";
    public const string EngineOff = "Start Daze Control to record controller buttons.";
    public const string TimedOut = "Nothing came through. Press the buttons together and try again.";
    public const string OldEngine = "This version of Daze Control can't record controller buttons yet. Update it and try again.";

    public static string Clash(GamepadChord chord) => $"Another flow uses {chord.Display}. Try another one.";
}

/// <summary>A chip under "Controller buttons": "View + Menu", with the validator's note when the chord is no good.</summary>
public sealed class ControllerButtonsViewModel(int index, string text, string? problem)
{
    public int Index { get; } = index;

    /// <summary>As people read it: "View + Menu" (the written text when it doesn't parse).</summary>
    public string Text { get; } = GamepadChord.TryParse(text, out var chord) ? chord.Display : text;

    public string? Problem { get; } = problem;

    public bool HasProblem => Problem is not null;

    public string RemoveName => $"Remove {Text}";

    public string AutomationName => Problem is null ? Text : $"{Text}. {Problem}";
}

public enum RecorderPhase
{
    Idle,
    Listening,
    Recorded,
    NoController,
    EngineOff,
    TimedOut,
    Refused,
}

/// <summary>The "Add buttons" flyout (UX §5.4): listens through <see cref="IGamepadRecorder"/> and hands a good chord on.</summary>
public sealed partial class GamepadRecorderViewModel(IGamepadRecorder recorder, Func<GamepadChord, bool> usedByOther, Action<GamepadChord> accept)
    : ObservableObject
{
    private CancellationTokenSource? _cancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening), nameof(Message), nameof(HasMessage), nameof(IsProblem))]
    public partial RecorderPhase Phase { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message), nameof(HasMessage))]
    public partial string? Refusal { get; private set; }

    public bool IsListening => Phase == RecorderPhase.Listening;

    public bool IsProblem => Phase is not (RecorderPhase.Idle or RecorderPhase.Listening or RecorderPhase.Recorded);

    /// <summary>What the flyout says: the prompt while listening, else why nothing was added.</summary>
    public string Message => Phase switch
    {
        RecorderPhase.Listening => GamepadTexts.Listening,
        RecorderPhase.NoController => GamepadTexts.NoController,
        RecorderPhase.EngineOff => GamepadTexts.EngineOff,
        RecorderPhase.TimedOut => GamepadTexts.TimedOut,
        RecorderPhase.Refused => Refusal ?? "",
        _ => "",
    };

    public bool HasMessage => Message.Length > 0;

    /// <summary>Opens the recorder: asks the engine for the next chord. A second call while listening does nothing.</summary>
    public async Task StartAsync()
    {
        if (IsListening)
            return;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        Phase = RecorderPhase.Listening;
        GamepadRecording result;
        try
        {
            result = await recorder.RecordAsync(cancel.Token).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            result = new(GamepadRecordStatus.Failed, Error: "Stopped.");
        }
        _cancel = null;
        if (cancel.IsCancellationRequested)
        {
            Phase = RecorderPhase.Idle;
            return;
        }
        Apply(result);
    }

    /// <summary>The flyout closed: the engine stops listening.</summary>
    public void Cancel()
    {
        _cancel?.Cancel();
        if (Phase == RecorderPhase.Listening)
            Phase = RecorderPhase.Idle;
    }

    private void Apply(GamepadRecording result)
    {
        switch (result.Status)
        {
            case GamepadRecordStatus.EngineOff:
                Phase = RecorderPhase.EngineOff;
                break;
            case GamepadRecordStatus.NoController:
                Phase = RecorderPhase.NoController;
                break;
            case GamepadRecordStatus.TimedOut:
                Phase = RecorderPhase.TimedOut;
                break;
            case GamepadRecordStatus.Chord when GamepadChord.TryParse(result.Chord, out var chord):
                if (usedByOther(chord))
                {
                    Refuse(GamepadTexts.Clash(chord));
                }
                else
                {
                    Phase = RecorderPhase.Recorded;
                    accept(chord);
                }
                break;
            case GamepadRecordStatus.Chord:
                Refuse(result.Chord is { } c && !c.Contains('+', StringComparison.Ordinal)
                    ? GamepadTexts.TooFew
                    : GamepadChord.Explain(result.Chord) ?? GamepadTexts.TooFew);
                break;
            default:
                Refuse(result.Error is { Length: > 0 } e ? e : GamepadTexts.TimedOut);
                break;
        }
    }

    private void Refuse(string why)
    {
        Refusal = why;
        Phase = RecorderPhase.Refused;
    }
}
