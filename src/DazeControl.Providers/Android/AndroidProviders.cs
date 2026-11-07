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
            var (exitCode, lastError, output) = await ProcessRunner.RunAsync(start, null, keepOutput: true, cancellationToken, outputLimit: 65536).ConfigureAwait(false);
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

    /// <summary>
    /// Opens an app by package id (D-094): <c>am start</c> for the phone launcher, then the TV one. <c>am</c> exits 0 when
    /// nothing could start, so its "Error" answer decides.
    /// </summary>
    public async Task StartAppAsync(AndroidDevice device, string package, CancellationToken cancellationToken)
    {
        if (!AndroidAppList.IsPackage(package))
            throw new StepFailedException($"\"{package}\" isn't an app's package name (like com.spotify.music).");
        var exe = device.Adb ?? DefaultAdb;
        var serial = device.Serial;
        await ConnectAsync(exe, serial, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var category in (string[])["LAUNCHER", "LEANBACK_LAUNCHER"])
        {
            var result = await adb.RunAsync(exe,
                ["-s", serial, "shell", $"am start -a android.intent.action.MAIN -c android.intent.category.{category} -p {package}"],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new StepFailedException($"The Android device refused to start {package}: {Reason(result)}");
            if (!(result.Output + result.LastError).Contains("Error", StringComparison.Ordinal))
                return;
        }
        throw new StepFailedException($"{package} isn't installed on the Android device \"{device.Key}\", or it has no icon to open.");
    }

    /// <summary>
    /// Ends an app by package id: <c>am force-stop</c>. It exits 0 for an app that isn't running or isn't installed, so
    /// "not running" is success, as for Close program.
    /// </summary>
    public async Task StopAppAsync(AndroidDevice device, string package, CancellationToken cancellationToken)
    {
        if (!AndroidAppList.IsPackage(package))
            throw new StepFailedException($"\"{package}\" isn't an app's package name (like com.spotify.music).");
        var exe = device.Adb ?? DefaultAdb;
        var serial = device.Serial;
        await ConnectAsync(exe, serial, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await adb.RunAsync(exe, ["-s", serial, "shell", $"am force-stop {package}"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new StepFailedException($"The Android device refused to stop {package}: {Reason(result)}");
    }

    public async Task ConnectAsync(string exe, string serial, CancellationToken cancellationToken)
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
        var hint = reason.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
            ? "Accept the debugging prompt on the device, or pair it again."
            : "Is it on, with network debugging turned on?";
        throw new StepFailedException($"Couldn't reach the Android device at {serial} ({reason}). {hint}");
    }

    /// <summary>The <c>android.connect</c> step (D-096): just the connect, with its retries; "already connected" counts as success.</summary>
    public Task ConnectAsync(AndroidDevice device, CancellationToken cancellationToken) =>
        ConnectAsync(device.Adb ?? DefaultAdb, device.Serial, cancellationToken);

    internal static string Reason(AdbResult result)
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
            ?? throw new StepFailedException(Library.Missing(LibraryKind.Android, key));
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

/// <summary><c>android.start</c>: opens the app (package id) on the device (D-094).</summary>
public sealed class AndroidStartProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.start";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var package = context.RequiredText("app").Trim();
        return remote.StartAppAsync(context.Device(), package, cancellationToken);
    }
}

/// <summary><c>android.stop</c>: force-stops the app (package id) on the device; not running counts as done.</summary>
public sealed class AndroidStopProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.stop";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        remote.StopAppAsync(context.Device(), context.RequiredText("app").Trim(), cancellationToken);
}

/// <summary><c>android.connect</c>: <c>adb connect</c> to the library device, so later steps (or other tools) find it (D-096).</summary>
public sealed class AndroidConnectProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.connect";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        remote.ConnectAsync(context.Device(), cancellationToken);
}
