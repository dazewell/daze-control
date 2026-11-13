using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Basic;

/// <summary>
/// <c>value.check</c> (T24.3, D-135): compares a text, already filled by the runner (D-129), with another. It fails with a
/// sentence when the comparison doesn't hold, so the failure link is the "else". Both sides lose spaces at the ends first.
/// "Is" and "isn't" compare as numbers when both sides are numbers (<c>5</c> is <c>5.0</c>), else as text; "at least" and
/// "below" need two numbers (invariant culture: <c>3.5</c>). Reads nothing from the system, holds nothing (idle = zero).
/// </summary>
public sealed class ValueCheckProvider : IStepProvider
{
    public string Id => ProviderCatalog.ValueCheckId;

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var value = (context.Text("value") ?? "").Trim();
        var to = (context.Text("to") ?? "").Trim();
        var ignoreCase = context.Boolean("ignoreCase", true);
        var op = context.Text("is") ?? "is";
        var (holds, sentence) = Compare(op, value, to, ignoreCase, context);
        context.SetValue("result", sentence); // given even when it doesn't hold: a failure link can say why
        if (!holds)
            throw new StepFailedException(sentence);
        context.SetSummary(sentence);
        return Task.CompletedTask;
    }

    private static (bool Holds, string Sentence) Compare(string op, string value, string to, bool ignoreCase, StepContext context)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var v = Quote(value);
        var t = Quote(to);
        switch (op)
        {
            case "is" or "isNot":
                var same = TryNumber(value, out var a) && TryNumber(to, out var b) ? a == b : string.Equals(value, to, comparison);
                return (op == "is" ? same : !same, $"{v} {(same ? "is" : "isn't")} {t}");
            case "contains" or "doesNotContain" or "startsWith" or "endsWith":
                if (to.Length == 0)
                    return Fail(context, "There is nothing to look for: fill in what to compare with.");
                var found = op switch
                {
                    "contains" or "doesNotContain" => value.Contains(to, comparison),
                    "startsWith" => value.StartsWith(to, comparison),
                    _ => value.EndsWith(to, comparison),
                };
                var words = op switch
                {
                    "contains" or "doesNotContain" => found ? "contains" : "doesn't contain",
                    "startsWith" => found ? "starts with" : "doesn't start with",
                    _ => found ? "ends with" : "doesn't end with",
                };
                return (op == "doesNotContain" ? !found : found, $"{v} {words} {t}");
            case "isEmpty" or "isNotEmpty":
                var empty = value.Length == 0;
                return (op == "isEmpty" ? empty : !empty, empty ? "The value is empty" : $"{v} isn't empty");
            case "atLeast" or "below":
                if (!TryNumber(value, out var x))
                    return Fail(context, $"{v} isn't a number, so it can't be compared with {(op == "atLeast" ? "at least" : "below")}.");
                if (!TryNumber(to, out var y))
                    return Fail(context, $"{t} isn't a number, so {v} can't be compared with it.");
                var atLeast = x >= y;
                return (op == "atLeast" ? atLeast : !atLeast, $"{value} is {(atLeast ? "at least" : "below")} {to}");
            default:
                return Fail(context, $"\"{op}\" isn't a way to compare.");
        }
    }

    private static (bool, string) Fail(StepContext context, string sentence)
    {
        context.SetValue("result", sentence);
        throw new StepFailedException(sentence);
    }

    /// <summary>A plain number (invariant culture, no NaN or infinity), after the caller trimmed it.</summary>
    private static bool TryNumber(string text, out double number) => StepValues.TryNumber(text, out number);

    private static string Quote(string text) => text.Length <= 60 ? $"\"{text}\"" : $"\"{text[..60]}…\"";
}
