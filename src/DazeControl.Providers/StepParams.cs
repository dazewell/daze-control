using System.Globalization;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>
/// Typed reads of a step's params: the written value, else the descriptor's default. Validation already checked
/// the text, so a parse failure here means the config changed shape under us; it fails the step, never the engine.
/// </summary>
public static class StepParams
{
    public static string? Text(this StepContext context, string name)
    {
        if (context.Node.With.TryGetValue(name, out var value) && value is ScalarParam scalar)
            return scalar.Text;
        return context.Descriptor.Params.FirstOrDefault(p => p.Name == name)?.Default;
    }

    public static string RequiredText(this StepContext context, string name)
    {
        var text = context.Text(name);
        return string.IsNullOrWhiteSpace(text) ? throw new StepFailedException($"\"{name}\" is missing.") : text;
    }

    public static IReadOnlyList<string> TextList(this StepContext context, string name)
    {
        if (context.Node.With.TryGetValue(name, out var value))
        {
            return value switch
            {
                ListParam list => [.. list.Items.Select(i => i.ToString())],
                ScalarParam scalar => [scalar.Text],
                _ => [],
            };
        }
        var fallback = context.Descriptor.Params.FirstOrDefault(p => p.Name == name)?.Default;
        return fallback is null ? [] : [fallback];
    }

    public static bool Boolean(this StepContext context, string name, bool fallback = false)
    {
        var text = context.Text(name);
        if (text is null)
            return fallback;
        return bool.TryParse(text, out var value) ? value : throw new StepFailedException($"\"{text}\" isn't true or false.");
    }

    public static TimeSpan Duration(this StepContext context, string name)
    {
        var text = context.RequiredText(name);
        return Core.Model.Duration.TryParse(text, out var value)
            ? value
            : throw new StepFailedException($"\"{text}\" isn't a duration (like 500ms, 20s, 1m30s).");
    }

    public static int WholeNumber(this StepContext context, string name, int fallback)
    {
        var text = context.Text(name);
        if (text is null)
            return fallback;
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new StepFailedException($"\"{text}\" isn't a whole number.");
    }
}
