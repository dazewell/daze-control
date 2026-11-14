using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers;

/// <summary>
/// A check step (D-081): reads the same <see cref="IFactProbe"/> a step's "Only if" uses and fails with the probe's
/// sentence ("Spotify isn't running") when the fact isn't what the <c>is</c> param asks for. Failure edges, retries and
/// fallbacks then work as for any step; <c>flag.check</c> is the precedent.
/// </summary>
/// <param name="positive">The <c>is</c> choice that means the fact is true ("running", "exists").</param>
public sealed class FactCheckProvider(string id, IFactProbe probe, string positive) : IStepProvider
{
    public string Id => id;

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var wanted = context.Text("is") == positive;
        var reading = await probe.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        context.SetValue("result", reading.Text); // given even when the check fails: a failure edge can say why
        if (reading.Value != wanted)
            throw new CheckNotMetException(reading.Text);
        context.SetSummary(reading.Text);
    }
}
