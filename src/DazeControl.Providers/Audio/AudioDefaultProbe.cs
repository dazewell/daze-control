using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Audio;

/// <summary><c>audio.isDefault</c>: the step's library device is the current default of its kind (read-only, D-081).</summary>
public sealed class AudioDefaultProbe(IAudioEndpoints audio) : IFactProbe
{
    public string Fact => "audio.isDefault";

    public async Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var device = AudioSteps.Device(context) ?? throw new StepFailedException("Choose a device.");
        var current = await audio.DefaultAsync(device.Flow, cancellationToken).ConfigureAwait(false);
        var kind = device.Flow.ToString().ToLowerInvariant();
        var isDefault = current is not null && current.Name.Contains(device.Match, StringComparison.OrdinalIgnoreCase);
        return new(isDefault, isDefault ? $"{device.Key} is the default {kind}" : $"{device.Key} isn't the default {kind}");
    }
}
