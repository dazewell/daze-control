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

    public async Task ShellAsync(AndroidDevice device, IReadOnlyList<string> commands, CancellationToken cancellationToken, string what = "the keys")
    {
        var (exe, serial) = await OpenAsync(device, cancellationToken).ConfigureAwait(false);
        var result = await adb.RunAsync(exe, ["-s", serial, "shell", string.Join(" && ", commands)], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new StepFailedException($"The Android device refused {what}: {Reason(result)}");
    }

    private async Task<(string Exe, string Serial)> OpenAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        var exe = device.Adb ?? DefaultAdb;
        await ConnectAsync(exe, device.Serial, cancellationToken).ConfigureAwait(false);
        // The timeout may have fired while connect drained its output: the lock is gone, so send nothing.
        cancellationToken.ThrowIfCancellationRequested();
        return (exe, device.Serial);
    }

    /// <summary>Android's volume streams by the step's choice word (<c>AudioManager.STREAM_*</c>).</summary>
    public static int VolumeStream(string name) => name switch
    {
        "system" => 1,
        "ring" => 2,
        "media" => 3,
        "alarm" => 4,
        "notification" => 5,
        _ => throw new StepFailedException($"\"{name}\" isn't a volume (media, ring, alarm, notification or system)."),
    };

    private static readonly string[] VolumeTools = ["cmd media_session volume", "media volume"];

    /// <summary>
    /// Sets a stream to a percent of the device's own range (D-105): read "[min..max]" with <c>--get</c>, then <c>--set</c>.
    /// <c>cmd media_session</c> (Android 8+) first, the older <c>media volume</c> as the fallback; neither = a plain sentence.
    /// </summary>
    public async Task SetVolumeAsync(AndroidDevice device, int stream, int percent, CancellationToken cancellationToken)
    {
        var (exe, serial) = await OpenAsync(device, cancellationToken).ConfigureAwait(false);
        var reason = "";
        foreach (var tool in VolumeTools)
        {
            var read = await adb.RunAsync(exe, ["-s", serial, "shell", $"{tool} --stream {stream} --get"], cancellationToken).ConfigureAwait(false);
            if (!Worked(read) || !TryParseRange(read.Output + "\n" + read.LastError, out var min, out var max))
            {
                reason = Reason(read);
                continue;
            }
            var level = min + (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * (max - min), MidpointRounding.AwayFromZero);
            var write = await adb.RunAsync(exe, ["-s", serial, "shell", $"{tool} --stream {stream} --set {level}"], cancellationToken).ConfigureAwait(false);
            if (Worked(write))
                return;
            reason = Reason(write);
        }
        throw new StepFailedException($"This Android device can't set its volume over adb (it needs Android 8 or newer, with media control allowed): {reason}");
    }

    /// <summary>Mute or unmute the media volume: an adjustment that is the same every time, unlike the mute key (a toggle).</summary>
    public async Task MuteAsync(AndroidDevice device, bool muted, CancellationToken cancellationToken)
    {
        var (exe, serial) = await OpenAsync(device, cancellationToken).ConfigureAwait(false);
        var reason = "";
        // AOSP's media_session --adj only knows raise/same/lower (it prints "Error:" and exits 0 for mute), so the audio service comes first.
        foreach (var command in (string[])[$"cmd audio {(muted ? "adj-mute" : "adj-unmute")} 3", $"cmd media_session volume --stream 3 --adj {(muted ? "mute" : "unmute")}"])
        {
            var result = await adb.RunAsync(exe, ["-s", serial, "shell", command], cancellationToken).ConfigureAwait(false);
            if (Worked(result))
                return;
            reason = Reason(result);
        }
        throw new StepFailedException($"This Android device can't {(muted ? "mute" : "unmute")} over adb (this Android version has no command for it; the Mute key is a toggle, so Send keys can do it once): {reason}");
    }

    /// <summary>Opens a web or app address in the device's default app (<c>am start -a VIEW -d</c>); <c>am</c> exits 0 on "Error".</summary>
    public async Task OpenAddressAsync(AndroidDevice device, string address, CancellationToken cancellationToken)
    {
        var url = address.Trim();
        if (!IsAddress(url))
            throw new StepFailedException($"\"{address}\" isn't an address with a scheme (like https://example.com).");
        var (exe, serial) = await OpenAsync(device, cancellationToken).ConfigureAwait(false);
        var result = await adb.RunAsync(exe, ["-s", serial, "shell", $"am start -a android.intent.action.VIEW -d '{url}'"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new StepFailedException($"The Android device refused to open {url}: {Reason(result)}");
        if (HasErrorLine(result))
            throw new StepFailedException($"No app on the Android device \"{device.Key}\" opens {url}.");
    }

    /// <summary>"scheme://rest" with no spaces, quotes or control characters: it goes inside single quotes in a shell command.</summary>
    internal static bool IsAddress(string url)
    {
        var colon = url.IndexOf("://", StringComparison.Ordinal);
        return colon > 0 && colon + 3 < url.Length
            && url[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            && url.All(c => c > ' ' && c != '\'' && c != '\u007f');
    }

    /// <summary>adb exits 0 for a shell command the device doesn't know; its text says so.</summary>
    internal static bool Worked(AdbResult result) =>
        result.ExitCode == 0 && !(result.Output + result.LastError).Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
        && !(result.Output + result.LastError).Contains("Exception", StringComparison.Ordinal)
        && !(result.Output + result.LastError).Contains("not found", StringComparison.OrdinalIgnoreCase)
        && !HasErrorLine(result);

    /// <summary>A line that starts with "Error" (am and cmd print these and still exit 0); "Error" inside an address doesn't count.</summary>
    internal static bool HasErrorLine(AdbResult result) =>
        (result.Output + "\n" + result.LastError).Split('\n').Any(l => l.TrimStart().StartsWith("Error", StringComparison.Ordinal));

    /// <summary>"volume is 7 in range [0..15]" → 0 and 15.</summary>
    internal static bool TryParseRange(string text, out int min, out int max)
    {
        min = max = 0;
        var open = text.IndexOf('[', StringComparison.Ordinal);
        var dots = open < 0 ? -1 : text.IndexOf("..", open, StringComparison.Ordinal);
        var close = dots < 0 ? -1 : text.IndexOf(']', dots);
        return close > 0
            && int.TryParse(text.AsSpan(open + 1, dots - open - 1).Trim(), out min)
            && int.TryParse(text.AsSpan(dots + 2, close - dots - 2).Trim(), out max)
            && max > min;
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

/// <summary><c>android.wake</c>: wakes the device (the pair of Sleep); a device that is awake stays awake.</summary>
public sealed class AndroidWakeProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.wake";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        remote.ShellAsync(context.Device(), [AndroidRemote.KeyEvent("wakeup")], cancellationToken, "to wake");
}

/// <summary><c>android.media</c>: one playback key (D-105), sent to whatever app is playing.</summary>
public sealed class AndroidMediaProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.media";

    /// <summary>The step's choice words and the keys they send.</summary>
    public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
    {
        ["playPause"] = "media_play_pause", ["play"] = "media_play", ["pause"] = "media_pause", ["stop"] = "media_stop",
        ["next"] = "media_next", ["previous"] = "media_previous", ["fastForward"] = "media_fast_forward", ["rewind"] = "media_rewind",
    };

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var action = context.RequiredText("action");
        if (!Keys.TryGetValue(action, out var key))
            throw new StepFailedException($"\"{action}\" isn't a playback action (play, pause, playPause, stop, next, previous, fastForward, rewind).");
        return remote.ShellAsync(context.Device(), [AndroidRemote.KeyEvent(key)], cancellationToken, "the playback key");
    }
}

/// <summary><c>android.setVolume</c>: a stream to a percent of the device's own range (D-105).</summary>
public sealed class AndroidSetVolumeProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.setVolume";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var percent = context.WholeNumber("percent", -1);
        if (percent is < 0 or > 100)
            throw new StepFailedException("Volume must be between 0 and 100 %.");
        return remote.SetVolumeAsync(context.Device(), AndroidRemote.VolumeStream(context.Text("stream") ?? "media"), percent, cancellationToken);
    }
}

/// <summary><c>android.changeVolume</c>: the volume key n times, up or down, in one adb call.</summary>
public sealed class AndroidChangeVolumeProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.changeVolume";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var key = context.RequiredText("direction") switch
        {
            "up" => "volume_up",
            "down" => "volume_down",
            var other => throw new StepFailedException($"\"{other}\" isn't up or down."),
        };
        var times = context.WholeNumber("times", 1);
        if (times is < 1 or > 30)
            throw new StepFailedException("Steps must be between 1 and 30.");
        return remote.ShellAsync(context.Device(), [.. Enumerable.Repeat(AndroidRemote.KeyEvent(key), times)], cancellationToken, "the volume keys");
    }
}

/// <summary><c>android.mute</c>: mute or unmute the media volume; repeating it changes nothing.</summary>
public sealed class AndroidMuteProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.mute";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        remote.MuteAsync(context.Device(), context.Boolean("muted", fallback: true), cancellationToken);
}

/// <summary><c>android.setBrightness</c>: manual brightness, a percent of the 0 to 255 setting.</summary>
public sealed class AndroidSetBrightnessProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.setBrightness";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var percent = context.WholeNumber("percent", -1);
        if (percent is < 0 or > 100)
            throw new StepFailedException("Brightness must be between 0 and 100 %.");
        var level = (int)Math.Round(percent * 255 / 100.0, MidpointRounding.AwayFromZero);
        return remote.ShellAsync(context.Device(),
            ["settings put system screen_brightness_mode 0", $"settings put system screen_brightness {level}"], cancellationToken, "the brightness");
    }
}

/// <summary><c>android.open</c>: opens an address (web page, video link, app link) in the device's default app.</summary>
public sealed class AndroidOpenProvider(AndroidRemote remote) : IStepProvider
{
    public string Id => "android.open";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        remote.OpenAddressAsync(context.Device(), context.RequiredText("address"), cancellationToken);
}
