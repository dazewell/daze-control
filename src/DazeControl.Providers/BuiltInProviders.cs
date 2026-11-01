using DazeControl.Providers.Basic;
using DazeControl.Providers.Process;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>The engine's composition root for step types (ARCHITECTURE → Adding a provider, step 3).</summary>
public static class BuiltInProviders
{
    public static IReadOnlyList<IStepProvider> Create(TimeProvider time, INotifier notifier)
    {
        var flags = new FlagStore();
        return
        [
            new WaitProvider(time),
            new NotifyProvider(notifier),
            new FlagSetProvider(flags),
            new FlagCheckProvider(flags),
            new ProcessRunProvider(),
            new PowerShellProvider(),
        ];
    }
}
