using DazeControl.Core.Catalog;
using DazeControl.Core.Model;

namespace DazeControl.Runtime.Providers;

/// <summary>
/// One attempt of one step. <paramref name="Attempt"/> starts at 1. Params are as written (<see cref="StepNode.With"/>);
/// validation already checked them against <paramref name="Descriptor"/>.
/// </summary>
public sealed record StepContext(Flow Flow, StepNode Node, ProviderDescriptor Descriptor, Library Library, int Attempt)
{
    /// <summary>Flow ids from the top run down to this flow (D-063): a start-mode <c>flow.run</c> passes it on.</summary>
    public IReadOnlyList<string> Chain { get; init; } = [Flow.Id];
}

/// <summary>
/// Runs one step type (ARCHITECTURE → Adding a provider). Success = the task completes; failure = it throws
/// (message shown to the user, so plain language: <see cref="StepFailedException"/>). Must honour the token: it
/// fires on timeout, on a stopped flow and on cancel. More members (validate, already-done, pickers) arrive with
/// the real providers in M4.
/// </summary>
public interface IStepProvider
{
    /// <summary>The <see cref="ProviderDescriptor.Id"/> this implements.</summary>
    string Id { get; }

    Task ExecuteAsync(StepContext context, CancellationToken cancellationToken);
}

/// <summary>An expected failure with a message fit for the pop-up ("Technics didn't connect").</summary>
public sealed class StepFailedException : Exception
{
    public StepFailedException()
    {
    }

    public StepFailedException(string message)
        : base(message)
    {
    }

    public StepFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
