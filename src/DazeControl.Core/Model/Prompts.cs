namespace DazeControl.Core.Model;

/// <summary>Shared words and limits of prompts (D-107): <c>ask: true</c> inputs and the <c>prompt.ask</c> step.</summary>
public static class Prompts
{
    /// <summary><c>onTimeout</c>: carry on as if the step wasn't there (<c>prompt.ask</c> only).</summary>
    public const string Skip = "skip";

    /// <summary><c>onTimeout</c>: end the run, noting "No answer".</summary>
    public const string Stop = "stop";

    /// <summary><c>onTimeout</c>: answer with the default.</summary>
    public const string UseDefault = "use-default";

    public const string Confirm = "confirm", Choose = "choose";

    /// <summary>A choose prompt lists 2 to this many labels (the flow menu's own limit).</summary>
    public const int MaxChoices = 12;

    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(5), MaxTimeout = TimeSpan.FromMinutes(10), DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The note history shows when a prompt ended its run for want of an answer.</summary>
    public const string NoAnswer = "No answer, so it didn't run.";

    /// <summary>
    /// True for a start nobody is there for (a schedule or an event): its unset <c>onTimeout</c> is <c>stop</c> and the
    /// run never waits on a person for long. Triggers are named "Schedule · 22:30" / "Event · PC unlocked" in history.
    /// </summary>
    public static bool IsNonInteractive(string trigger) =>
        trigger.StartsWith("Schedule", StringComparison.Ordinal) || trigger.StartsWith("Event", StringComparison.Ordinal);

    /// <summary>What happens at the timeout: the written choice, else <c>stop</c> for a start nobody is there for, else <paramref name="interactiveDefault"/>.</summary>
    public static string OnTimeoutOf(string? written, string trigger, string interactiveDefault) =>
        written is { Length: > 0 } ? written : IsNonInteractive(trigger) ? Stop : interactiveDefault;
}
