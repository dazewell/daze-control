using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Basic;

/// <summary>Where <c>notify</c> text goes: the engine shows it (tray balloon now, the run pop-up from M5).</summary>
public interface INotifier
{
    /// <param name="sound">False shows it without the Windows notification sound (F-037).</param>
    void Show(string flowName, string text, bool sound);
}

/// <summary><c>notify</c>: shows a message. Fire-and-forget: the step succeeds once the text is handed over.</summary>
public sealed class NotifyProvider(INotifier notifier) : IStepProvider
{
    public string Id => "notify";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        notifier.Show(context.Flow.Name, context.RequiredText("text"), context.Boolean("sound", fallback: true));
        return Task.CompletedTask;
    }
}
