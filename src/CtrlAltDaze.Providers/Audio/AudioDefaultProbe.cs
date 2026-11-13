using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Audio;

/// <summary><c>audio.isDefault</c>: the step's library device is the current default of its kind (read-only, D-081).</summary>
public sealed class AudioDefaultProbe(IAudioEndpoints audio) : IFactProbe
{
    public string Fact => "audio.isDefault";

    public async Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var device = AudioSteps.Device(context) ?? throw new StepFailedException("Choose a device.");
        // Every role the step would set must already be this device (a Set default step's "Use for"; a check has none = all).
        var roles = context.Descriptor.FindParam("roles") is null ? AudioRoles.All : AudioSteps.Roles(context);
        var current = await audio.DefaultsAsync(device.Flow, roles, cancellationToken).ConfigureAwait(false);
        var kind = device.Flow.ToString().ToLowerInvariant();
        var isDefault = current.All(e => e is not null && e.Name.Contains(device.Match, StringComparison.OrdinalIgnoreCase));
        return new(isDefault, isDefault ? $"{device.Key} is the default {kind}" : $"{device.Key} isn't the default {kind}");
    }
}
