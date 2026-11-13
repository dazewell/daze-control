using System.Globalization;
using DazeControl.Core.Catalog;

namespace DazeControl.Core.Model;

/// <summary>
/// "Repeat" on a <c>flow.run</c> that waits (T27.6, D-151): run the flow up to <see cref="MaxRounds"/> times, until a round works
/// or until one fails, with a pause between rounds. One reader for the validator and the runner, so both say the same.
/// </summary>
public static class FlowRepeat
{
    public const string RepeatParam = "repeat";
    public const string UntilParam = "until";
    public const string PauseParam = "pause";
    public const string UntilWorks = "works";
    public const string UntilFails = "fails";

    /// <summary>A bound so a flow can't spin: 50 rounds, each with its own time limit.</summary>
    public const int MaxRounds = 50;

    public static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(10);

    /// <summary>What a step's settings say, after values were filled.</summary>
    /// <param name="Rounds">1 = no repeat.</param>
    /// <param name="UntilWorks">Stop at the first round that works (else at the first that fails).</param>
    public readonly record struct Plan(int Rounds, bool UntilWorks, TimeSpan Pause);

    /// <summary>
    /// Reads <paramref name="step"/>'s repeat settings; null with <paramref name="problem"/> set when one is unusable. The sentences
    /// never repeat the setting's text: it may come from a Sensitive value.
    /// </summary>
    public static Plan? Read(StepNode step, out string? problem) => Read(step, out problem, out _);

    /// <param name="step">A <c>flow.run</c> step, values filled (the runner) or as written (the validator).</param>
    /// <param name="problem">Why the settings can't run, or null.</param>
    /// <param name="param">The setting <paramref name="problem"/> is about.</param>
    public static Plan? Read(StepNode step, out string? problem, out string? param)
    {
        ArgumentNullException.ThrowIfNull(step);
        problem = null;
        param = null;
        var rounds = 1;
        if (Text(step, RepeatParam) is { } repeat)
        {
            if (repeat.Length == 0)
                problem = "Repeat has no number: the value it uses was empty.";
            else if (!StepValues.TryNumber(repeat, out var n) || n != Math.Floor(n))
                problem = "Repeat isn't a whole number.";
            else if (n < 1 || n > MaxRounds)
                problem = $"Repeat must be 1 to {MaxRounds.ToString(CultureInfo.InvariantCulture)} times.";
            else
                rounds = (int)n;
            param = problem is null ? null : RepeatParam;
        }
        var until = Text(step, UntilParam) is { Length: > 0 } u ? u : UntilWorks;
        if (problem is null && until is not (UntilWorks or UntilFails))
            (problem, param) = ($"Repeat until must be \"{UntilWorks}\" or \"{UntilFails}\".", UntilParam);
        var pause = TimeSpan.Zero;
        if (problem is null && Text(step, PauseParam) is { Length: > 0 } p)
        {
            if (!Duration.TryParse(p, out pause))
                problem = "The wait between rounds isn't a duration (like 500ms, 20s, 1m30s).";
            else if (pause < TimeSpan.Zero || pause > MaxPause)
                problem = "Wait 0 to 10m between rounds.";
            param = problem is null ? null : PauseParam;
        }
        return problem is null ? new Plan(rounds, until == UntilWorks, pause) : null;
    }

    /// <summary>The step repeats: a written number over 1 (a value counts too: it may be over 1 when the step runs).</summary>
    public static bool Repeats(StepNode step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Text(step, RepeatParam) is { Length: > 0 } text
            && (StepValues.WholeRef(text) is not null || (StepValues.TryNumber(text, out var n) && n > 1));
    }

    /// <summary>The step's line in Runs: how many rounds ran and why it stopped.</summary>
    /// <param name="cancelled">Stop came between rounds: says how many ran, not how the plan would have ended.</param>
    public static string? Summary(Plan plan, int ran, bool worked, bool cancelled = false)
    {
        if (plan.Rounds <= 1)
            return null;
        var of = $"{ran.ToString(CultureInfo.InvariantCulture)} of {plan.Rounds.ToString(CultureInfo.InvariantCulture)}";
        if (cancelled)
            return $"Stopped after round {of}";
        return plan.UntilWorks
            ? worked ? $"Worked on round {of}" : $"Didn't work in {plan.Rounds.ToString(CultureInfo.InvariantCulture)} rounds"
            : worked ? $"Ran {plan.Rounds.ToString(CultureInfo.InvariantCulture)} times" : $"Stopped at round {of}: it didn't work";
    }

    /// <summary>A repeat setting holds a value reference: only the runner can read it (D-146).</summary>
    public static bool TakesValues(StepNode step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new[] { RepeatParam, UntilParam, PauseParam }.Any(name => StepValues.Parse(Text(step, name)).Count > 0);
    }

    private static string? Text(StepNode step, string name) =>
        step.With.TryGetValue(name, out var value) && value is ScalarParam scalar ? scalar.Text.Trim() : null;
}
