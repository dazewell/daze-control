using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Basic;

/// <summary>Where <c>notify</c> text goes: the engine shows it (tray balloon now, the run pop-up from M5).</summary>
public interface INotifier
{
    void Show(string flowName, string text);
}

/// <summary><c>notify</c>: shows a message. Fire-and-forget: the step succeeds once the text is handed over.</summary>
public sealed class NotifyProvider(INotifier notifier) : IStepProvider
{
    public string Id => "notify";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        notifier.Show(context.Flow.Name, context.RequiredText("text"));
        return Task.CompletedTask;
    }
}
