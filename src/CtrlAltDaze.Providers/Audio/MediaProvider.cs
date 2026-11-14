using System.Runtime.InteropServices;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Audio;

/// <summary>A playback command for this PC; the value is its <c>APPCOMMAND_*</c> number.</summary>
public enum MediaCommand
{
    NextTrack = 11,
    PreviousTrack = 12,
    Stop = 13,
    PlayPause = 14,
    Play = 46,
    Pause = 47,
}

/// <summary>What carries a playback command to the PC's media app; a seam so tests never touch the user's music.</summary>
public interface IMediaCommands
{
    /// <summary>Hands <paramref name="command"/> over; returns when Windows took it.</summary>
    void Send(MediaCommand command);
}

/// <summary>
/// WM_APPCOMMAND to the shell's taskbar window, the message a media key becomes: Windows passes it on to the app that owns
/// media. No key simulation (nothing reaches the focused window), no focus change, no admin, nothing resident (D-138).
/// </summary>
public sealed partial class WindowsMediaCommands : IMediaCommands
{
    private const uint WmAppCommand = 0x0319;
    private const uint AbortIfHung = 0x0002;

    public void Send(MediaCommand command)
    {
        var shell = FindWindowW("Shell_TrayWnd", null);
        if (shell == 0)
            throw new StepFailedException("Windows' taskbar isn't running, so there is nobody to pass the playback command to.");
        // The command sits in the high word of lParam; the low word (0) says it came from a key.
        var result = SendMessageTimeoutW(shell, WmAppCommand, shell, (nint)((int)command << 16), AbortIfHung, 2000, out _);
        if (result == 0)
            throw new StepFailedException($"Windows didn't take the playback command (error {Marshal.GetLastPInvokeError()}).");
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(nint hWnd, uint message, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);
}

/// <summary><c>audio.media</c>: play, pause, stop, next or previous on this PC. It can't tell whether anything played (D-138).</summary>
public sealed class AudioMediaProvider(IMediaCommands media) : IStepProvider
{
    public string Id => "audio.media";

    /// <summary>The step's choice words and the commands they send.</summary>
    public static readonly IReadOnlyDictionary<string, MediaCommand> Commands = new Dictionary<string, MediaCommand>(StringComparer.Ordinal)
    {
        ["playPause"] = MediaCommand.PlayPause, ["play"] = MediaCommand.Play, ["pause"] = MediaCommand.Pause,
        ["stop"] = MediaCommand.Stop, ["next"] = MediaCommand.NextTrack, ["previous"] = MediaCommand.PreviousTrack,
    };

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var action = context.RequiredText("action");
        if (!Commands.TryGetValue(action, out var command))
            throw new StepFailedException($"\"{action}\" isn't a playback action ({string.Join(", ", Commands.Keys)}).");
        cancellationToken.ThrowIfCancellationRequested();
        media.Send(command);
        return Task.CompletedTask;
    }
}
