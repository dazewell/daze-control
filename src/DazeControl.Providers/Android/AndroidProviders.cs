using System.Diagnostics;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Providers.Process;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Android;

/// <summary>What one adb.exe call printed.</summary>
public sealed record AdbResult(int ExitCode, string Output, string LastError);

/// <summary>adb.exe behind a seam, so the steps are tested without a TV (D-032).</summary>
public interface IAdb
{
    Task<AdbResult> RunAsync(string adb, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>The real adb.exe, run hidden; a missing adb fails the step in plain words.</summary>
public sealed class AdbProcess : IAdb
{
    public async Task<AdbResult> RunAsync(string adb, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(adb);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        try
        {
            var (exitCode, lastError, output) = await ProcessRunner.RunAsync(start, null, keepOutput: true, cancellationToken).ConfigureAwait(false);
            return new AdbResult(exitCode, output, lastError);
        }
        catch (StepFailedException e) when (e.InnerException is System.ComponentModel.Win32Exception)
        {
            throw new StepFailedException(adb == AndroidRemote.DefaultAdb
                ? "adb isn't installed or isn't on PATH. Install Android platform-tools, or set adb: in the Android device's library entry."
                : $"Couldn't start {adb}: {e.InnerException.Message}");
        }
    }
}

/// <summary>
/// One Android device over adb, ported from scd's AndroidModule: connect (with retries, since a sleeping TV answers late),
/// then one chained <c>adb shell</c> call per step, so key presses don't pay a round trip each.
/// </summary>
public sealed class AndroidRemote(IAdb adb, TimeProvider time)
{
    public const string DefaultAdb = "adb";
    private const int ConnectAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public async Task ShellAsync(AndroidDevice device, IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        var exe = device.Adb ?? DefaultAdb;
        var serial = device.Serial;
        await ConnectAsync(exe, serial, cancellationToken).ConfigureAwait(false);
        // The timeout may have fired while connect drained its output: the lock is gone, so send no keys.
        cancellationToken.ThrowIfCancellationRequested();
        var result = await adb.RunAsync(exe, ["-s", serial, "shell", string.Join(" && ", commands)], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new StepFailedException($"The Android device refused the keys: {Reason(result)}");
    }

    private async Task ConnectAsync(string exe, string serial, CancellationToken cancellationToken)
    {
        string reason = "";
        for (var attempt = 1; attempt <= ConnectAttempts; attempt++)
        {
            var result = await adb.RunAsync(exe, ["connect", serial], cancellationToken).ConfigureAwait(false);
            // adb connect exits 0 even when it fails; only its answer tells ("connected to", "already connected to").
            if (result.ExitCode == 0 && result.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                && !result.Output.Contains("failed", StringComparison.OrdinalIgnoreCase))
                return;
            reason = Reason(result);
            if (attempt < ConnectAttempts)
                await Task.Delay(RetryDelay, time, cancellationToken).ConfigureAwait(false);
        }
        throw new StepFailedException($"Couldn't reach the Android device at {serial} ({reason}). Is it on, with network debugging turned on?");
    }

    private static string Reason(AdbResult result)
    {
        var text = result.LastError.Length > 0 ? result.LastError : result.Output;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return line ?? $"adb exit code {result.ExitCode}";
    }

    /// <summary>"home" → "KEYCODE_HOME"; letters, digits and _ only, since the keys go into a shell command.</summary>
    public static string KeyCode(string key)
    {
        if (AndroidKeys.Code(key) is { } code)
            return code;
        var name = key.Trim().ToUpperInvariant();
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new StepFailedException($"\"{key}\" isn't an Android key name (like home, back, dpad_down).");
        return name.StartsWith("KEYCODE_", StringComparison.Ordinal) ? name : "KEYCODE_" + name;
    }

    public static string KeyEvent(string key) => "input keyevent " + KeyCode(key);
}

internal static class AndroidSteps
{
    public static AndroidDevice Device(this StepContext context)
    {
        var key = context.RequiredText("device");
        return context.Library.Android.FirstOrDefault(d => d.Key == key)
            ?? throw new StepFailedException($"There's no Android device called \"{key}\" in the library.");
    }
}

/// <summary><c>android.input</c>: wake, open the input list, move to the saved input, choose it (scd's sequence).</summary>
public sealed class AndroidInputProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.input";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var device = context.Device();
        var input = context.RequiredText("input");
        if (!device.Inputs.TryGetValue(input, out var moves))
        {
            var known = device.Inputs.Count == 0 ? "none saved yet" : string.Join(", ", device.Inputs.Keys);
            throw new StepFailedException($"The Android device \"{device.Key}\" has no input called \"{input}\" ({known}).");
        }
        return remote.ShellAsync(device, Commands(moves), cancellationToken);
    }

    public static IReadOnlyList<string> Commands(IEnumerable<string> moves) =>
    [
        AndroidRemote.KeyEvent("wakeup"),
        AndroidRemote.KeyEvent("tv_input"),
        .. moves.Select(m => AndroidRemote.KeyEvent(m == "up" ? "dpad_up" : "dpad_down")),
        AndroidRemote.KeyEvent("dpad_center"),
    ];
}

/// <summary><c>android.keys</c>: the keys in order, in one adb call.</summary>
public sealed class AndroidKeysProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.keys";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var device = context.Device();
        var keys = context.TextList("keys");
        if (keys.Count == 0)
            throw new StepFailedException("Say which keys to send.");
        return remote.ShellAsync(device, [.. keys.Select(AndroidRemote.KeyEvent)], cancellationToken);
    }
}

/// <summary><c>android.sleep</c>: the TV (and, over HDMI-CEC, usually the screen) goes to sleep.</summary>
public sealed class AndroidSleepProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.sleep";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        remote.ShellAsync(context.Device(), [AndroidRemote.KeyEvent("sleep")], cancellationToken);
}
