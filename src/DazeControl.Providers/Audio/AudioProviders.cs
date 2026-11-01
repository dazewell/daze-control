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
        return context.Library.Audio.FirstOrDefault(d => d.Key == key)
            ?? throw new StepFailedException($"No audio device called \"{key}\" in the library.");
    }

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
        var roles = context.Text("roles") switch
        {
            "console" => AudioRoles.Console,
            "communications" => AudioRoles.Communications,
            _ => AudioRoles.All,
        };
        var endpoint = await audio.WaitForAsync(device.Match, device.Flow, cancellationToken).ConfigureAwait(false);
        await audio.SetDefaultAsync(endpoint.Id, roles, cancellationToken).ConfigureAwait(false);
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
