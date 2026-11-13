using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Audio;

public sealed record AudioEndpoint(string Id, string Name);

/// <summary>Which default slots <c>audio.setDefault</c> fills (ERole: console + multimedia, communications).</summary>
[Flags]
public enum AudioRoles
{
    Console = 1,
    Communications = 2,
    All = Console | Communications,
}

/// <summary>Core Audio behind a seam so the steps test with a fake (ARCHITECTURE → Adding a provider).</summary>
public interface IAudioEndpoints
{
    /// <summary>The first active endpoint whose name contains <paramref name="match"/>; waits for it to appear
    /// (Bluetooth just connected) until the token fires.</summary>
    Task<AudioEndpoint> WaitForAsync(string match, AudioFlow flow, CancellationToken cancellationToken);

    Task<AudioEndpoint?> DefaultAsync(AudioFlow flow, CancellationToken cancellationToken);

    /// <summary>The current default of each role <paramref name="roles"/> stands for (read-only), in the order
    /// <c>SetDefaultAsync</c> sets them; null where there is none.</summary>
    Task<IReadOnlyList<AudioEndpoint?>> DefaultsAsync(AudioFlow flow, AudioRoles roles, CancellationToken cancellationToken);

    Task SetDefaultAsync(string id, AudioRoles roles, CancellationToken cancellationToken);

    Task SetVolumeAsync(string id, float level, CancellationToken cancellationToken);

    Task SetMuteAsync(string id, bool muted, CancellationToken cancellationToken);
}

internal static class AudioSteps
{
    public static AudioDevice? Device(StepContext context)
    {
        if (context.Text("device") is not { Length: > 0 } key)
            return null;
        return context.Library.ResolveAudio(key)
            ?? throw new StepFailedException(MissingSide(context.Library, key));
    }

    /// <summary>A bound device whose audio side is gone says so; else the usual missing item (D-116).</summary>
    private static string MissingSide(Library library, string key) =>
        library.Bound.FirstOrDefault(b => b.Key == key) is { } bound
            ? $"The bound device \"{key}\" has no audio device \"{bound.Audio}\" in your Library."
            : Library.Missing(LibraryKind.Audio, key);

    public static AudioRoles Roles(StepContext context) => context.Text("roles") switch
    {
        "console" => AudioRoles.Console,
        "communications" => AudioRoles.Communications,
        _ => AudioRoles.All,
    };

    /// <summary>The named device (waiting for it), else the current default output.</summary>
    public static async Task<AudioEndpoint> TargetAsync(IAudioEndpoints audio, StepContext context, CancellationToken cancellationToken)
    {
        if (Device(context) is { } device)
            return await audio.WaitForAsync(device.Match, device.Flow, cancellationToken).ConfigureAwait(false);
        return await audio.DefaultAsync(AudioFlow.Output, cancellationToken).ConfigureAwait(false)
            ?? throw new StepFailedException("There's no default output device.");
    }
}

/// <summary><c>audio.setDefault</c>: makes a library device the default, waiting for it to appear first.</summary>
public sealed class AudioSetDefaultProvider(IAudioEndpoints audio) : IStepProvider
{
    public string Id => "audio.setDefault";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var device = AudioSteps.Device(context) ?? throw new StepFailedException("Choose a device.");
        var roles = AudioSteps.Roles(context);
        var endpoint = await audio.WaitForAsync(device.Match, device.Flow, cancellationToken).ConfigureAwait(false);
        // D-130: read the default it replaces before changing it, only when a later step uses it (one COM read).
        var previous = context.Wants(Previous)
            ? (await audio.DefaultsAsync(device.Flow, roles, cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e is not null)
            : null;
        await audio.SetDefaultAsync(endpoint.Id, roles, cancellationToken).ConfigureAwait(false);
        context.SetDevice("device", context.RequiredText("device"), LibraryKind.Audio);
        if (previous is not null && KeyOf(context.Library, previous.Name, device.Flow) is { } key)
            context.SetDevice(Previous, key, LibraryKind.Audio);
    }

    public const string Previous = "previous";

    /// <summary>
    /// The Library audio device an endpoint is (D-130): the one whose match its name contains, in the same direction, as
    /// <c>WaitForAsync</c> finds endpoints; the longest match wins ("Technics EAH" over "Technics"), then Library order.
    /// Null when no Library item matches: a device outside the Library can't fill a device setting.
    /// </summary>
    public static string? KeyOf(Library library, string endpointName, AudioFlow flow)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.Audio
            .Where(d => d.Flow == flow && d.Match.Length > 0 && endpointName.Contains(d.Match, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Match.Length)
            .FirstOrDefault()?.Key;
    }
}

/// <summary><c>audio.setVolume</c>: sets the master volume of a device (or the default output).</summary>
public sealed class AudioSetVolumeProvider(IAudioEndpoints audio) : IStepProvider
{
    public string Id => "audio.setVolume";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var level = Math.Clamp(context.WholeNumber("percent", 0), 0, 100) / 100f;
        var endpoint = await AudioSteps.TargetAsync(audio, context, cancellationToken).ConfigureAwait(false);
        await audio.SetVolumeAsync(endpoint.Id, level, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>audio.mute</c>: mutes or unmutes a device (or the default output).</summary>
public sealed class AudioMuteProvider(IAudioEndpoints audio) : IStepProvider
{
    public string Id => "audio.mute";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var muted = context.Boolean("muted", fallback: true);
        var endpoint = await AudioSteps.TargetAsync(audio, context, cancellationToken).ConfigureAwait(false);
        await audio.SetMuteAsync(endpoint.Id, muted, cancellationToken).ConfigureAwait(false);
    }
}
