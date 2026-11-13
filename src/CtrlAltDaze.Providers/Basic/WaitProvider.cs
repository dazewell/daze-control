using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Basic;

/// <summary><c>wait</c>: a pause between steps; the token ends it early on stop or timeout.</summary>
public sealed class WaitProvider(TimeProvider time) : IStepProvider
{
    public string Id => "wait";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        Task.Delay(context.Duration("duration"), time, cancellationToken);
}
