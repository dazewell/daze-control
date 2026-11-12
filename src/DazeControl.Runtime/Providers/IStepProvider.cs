using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime.Providers;

/// <summary>
/// One attempt of one step. <paramref name="Attempt"/> starts at 1. Params are as written (<see cref="StepNode.With"/>);
/// validation already checked them against <paramref name="Descriptor"/>.
/// </summary>
public sealed record StepContext(Flow Flow, StepNode Node, ProviderDescriptor Descriptor, Library Library, int Attempt)
{
    /// <summary>Flow ids from the top run down to this flow (D-063): a start-mode <c>flow.run</c> passes it on.</summary>
    public IReadOnlyList<string> Chain { get; init; } = [Flow.Id];

    /// <summary>What started the run (history's words, "Schedule · 22:30"); empty in tests. Prompts read it (D-107).</summary>
    public string Trigger { get; init; } = "";

    /// <summary>What the step says about itself, read by the runner after the attempt (<see cref="SetSummary"/>).</summary>
    public string? Summary { get; private set; }

    /// <summary>Words for <c>NodeResult.Summary</c> and the node's finish event ("Picked Couch TV"); the last call wins.</summary>
    public void SetSummary(string summary) => Summary = summary;

    /// <summary>The Library key a pick step picked (D-117), read by the runner after the attempt; later steps use it as <c>$result:id</c>.</summary>
    public string? Result => Values.TryGetValue(StepValues.DeviceName, out var device) && device.Kind == ValueKind.Device ? device.Text : null;

    /// <summary><c>SetDevice("device", key)</c>, kept for pick steps.</summary>
    public void SetResult(string key) => SetDevice(StepValues.DeviceName, key);

    private readonly Dictionary<string, StepValue> _values = new(StringComparer.Ordinal);

    /// <summary>Whether a later step uses this value (D-129): a provider skips costly work (reading stdout) when none does. True where unknown.</summary>
    public Func<string, bool>? WantsValue { get; init; }

    public bool Wants(string name) => WantsValue?.Invoke(name) ?? true;

    /// <summary>A text value later steps can use (<c>{id.name}</c>); the runner takes it after the attempt (D-129). The last call wins.</summary>
    public void SetValue(string name, string text) => Put(name, new(ValueKind.Text, text ?? ""));

    public void SetNumber(string name, long number) =>
        Put(name, new(ValueKind.Number, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public void SetNumber(string name, double number) =>
        Put(name, new(ValueKind.Number, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A Library key as a device value; <paramref name="kind"/> says which list it is in (null = the value's declared kind).</summary>
    public void SetDevice(string name, string key, LibraryKind? kind = null) => Put(name, new(ValueKind.Device, key, kind));

    private void Put(string name, StepValue value)
    {
        lock (_values)
            _values[name] = value;
    }

    /// <summary>What the attempt gave, as a snapshot: a provider that ignores cancellation may still write after the runner moved on.</summary>
    public IReadOnlyDictionary<string, StepValue> Values
    {
        get
        {
            lock (_values)
                return new Dictionary<string, StepValue>(_values, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Runs another flow nested inside this step, the way a waiting <c>flow.run</c> does (D-062); null where the runner
    /// has no flows (dry run). The nested run is kept on the node (<see cref="Called"/>). Honour the step's token.
    /// </summary>
    public Func<string, CancellationToken, Task<NestedAnswer>>? RunNested { get; init; }

    /// <summary>The nested run <see cref="RunNested"/> made in this attempt; set by the runner.</summary>
    public CalledRun? Called { get; set; }
}

/// <summary>How a nested run went, for the step that asked.</summary>
/// <param name="Error">Why the step fails (a failed child, a loop, a flow that can't run); null = it finished.</param>
/// <param name="Quiet">The nested flow ended quietly (a menu with nothing picked): so does the step's flow.</param>
public sealed record NestedAnswer(string? Error, bool Quiet);

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

/// <summary>
/// A step ends its whole flow on purpose, without a failure (a menu closed with nothing picked, D-054). The runner
/// marks the node Cancelled with the message, cancels the rest of the run, and tells the pop-up to show nothing.
/// </summary>
public sealed class StepEndsFlowException : Exception
{
    public StepEndsFlowException()
    {
    }

    public StepEndsFlowException(string message)
        : base(message)
    {
    }

    public StepEndsFlowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
