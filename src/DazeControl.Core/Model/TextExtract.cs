using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DazeControl.Core.Model;

/// <summary>
/// <c>text.extract</c> (T25.2, D-143): takes one part of a text. The settings check (<see cref="Check"/>) and the run
/// (<see cref="Extract"/>) are shared by the validator and the provider, so what the editor accepts is what the step does.
/// Every limit is here: the text is at most <see cref="MaxText"/> characters, a pattern <see cref="MaxPattern"/>, a path
/// <see cref="MaxPath"/>, JSON deeper than <see cref="MaxJsonDepth"/> is refused, and a pattern gets <see cref="PatternTimeout"/>.
/// Sentences name the setting that was asked for, never the text it was asked of.
/// </summary>
public static class TextExtract
{
    /// <summary>The longest text it reads (64 KB of characters). Longer fails; it is never silently cut.</summary>
    public const int MaxText = 65536;

    /// <summary>What a setting filled for it, and a value kept for it, is cut at: one over <see cref="MaxText"/>, so the cut shows and the step refuses.</summary>
    public const int MaxFilled = MaxText + 1;

    public const int MaxPattern = 500, MaxPath = 200, MaxPathParts = 32, MaxJsonDepth = 64, MaxLine = 1_000_000;

    /// <summary>A pattern that takes longer than this on one text is stopped (ReDoS).</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(500);

    public static readonly string[] Modes = ["json", "pattern", "between", "line"];

    /// <summary>What a run gave: the part, or the sentence for why there is none.</summary>
    public readonly record struct Result(string? Value, string? Failure);

    /// <summary>
    /// What is wrong with the settings themselves, or null. <paramref name="find"/> and <paramref name="until"/> are as written
    /// (or filled); they are trimmed here first, like the run does. For "between" either may be empty, not both.
    /// </summary>
    public static string? Check(string? by, string? find, string? until)
    {
        find = (find ?? "").Trim();
        until = (until ?? "").Trim();
        switch ((by ?? "json").Trim())
        {
            case "json":
                return find.Length == 0 ? "Fill in which field to take, like data.items[0].name." : TryPath(find, out _, out var why) ? null : why;
            case "pattern":
                if (find.Length == 0)
                    return "Fill in the pattern to look for.";
                return TryPattern(find, out _, out var bad) ? null : bad;
            case "between":
                return find.Length == 0 && until.Length == 0 ? "Fill in the text that comes before the part you want, after it, or both." : null;
            case "line":
                return TryLine(find, out _) ? null : $"Use a line number from 1 to {MaxLine:N0}, like 3.";
            default:
                return "Choose what to take: a JSON field, a pattern, what is between two texts, or a line.";
        }
    }

    /// <summary>Takes the part. Settings that <see cref="Check"/> refuses fail here with the same sentence.</summary>
    public static Result Extract(string? by, string? text, string? find, string? until)
    {
        if (Check(by, find, until) is { } why)
            return new(null, why);
        text ??= "";
        if (text.Length > MaxText)
            return new(null, "The text is longer than 64 KB, which is more than this step reads.");
        find = (find ?? "").Trim();
        until = (until ?? "").Trim();
        return (by ?? "json").Trim() switch
        {
            "json" => FromJson(text, find),
            "pattern" => FromPattern(text, find),
            "between" => FromBetween(text, find, until),
            _ => FromLine(text, find),
        };
    }

    // ---- JSON ----

    private readonly record struct Part(string? Name, int Index);

    /// <summary>
    /// A path like <c>data.items[0].name</c>: names between dots, <c>[n]</c> for an array item, <c>["name"]</c> for a name that
    /// holds a dot or a bracket. A leading <c>$</c> is allowed (<c>$.data</c>). Spaces at the ends don't count.
    /// </summary>
    private static bool TryPath(string path, out List<Part> parts, out string why)
    {
        parts = [];
        why = $"That isn't a field path. Use names with dots and [numbers], like data.items[0].name.";
        path = path.Trim();
        if (path.Length > MaxPath)
        {
            why = $"The field path is longer than {MaxPath} characters.";
            return false;
        }
        var i = 0;
        if (path.StartsWith('$'))
        {
            i = 1;
            if (i < path.Length && path[i] == '.')
                i++;
            else if (i < path.Length && path[i] != '[')
                return false;
        }
        var expectName = true; // at the start, or after a dot
        while (i < path.Length)
        {
            if (parts.Count >= MaxPathParts)
            {
                why = $"The field path has more than {MaxPathParts} parts.";
                return false;
            }
            if (path[i] == '[')
            {
                if (expectName && parts.Count > 0)
                    return false; // "a.[0]"
                var close = path.IndexOf(']', i);
                if (close < 0)
                    return false;
                var inner = path[(i + 1)..close];
                if (inner.Length >= 2 && inner[0] == '"' && inner[^1] == '"' && inner.Length > 2 && !inner[1..^1].Contains('"', StringComparison.Ordinal))
                    parts.Add(new(inner[1..^1], 0));
                else if (inner.Length is > 0 and <= 9 && inner.All(char.IsAsciiDigit))
                    parts.Add(new(null, int.Parse(inner, CultureInfo.InvariantCulture)));
                else
                    return false;
                i = close + 1;
                expectName = false;
                if (i < path.Length && path[i] == '.')
                {
                    i++;
                    expectName = true;
                    if (i >= path.Length)
                        return false;
                }
                else if (i < path.Length && path[i] != '[')
                    return false;
                continue;
            }
            if (!expectName && parts.Count > 0)
                return false;
            var start = i;
            while (i < path.Length && path[i] is not ('.' or '[' or ']'))
                i++;
            var name = path[start..i];
            if (name.Length == 0)
                return false;
            parts.Add(new(name, 0));
            expectName = false;
            if (i < path.Length && path[i] == '.')
            {
                i++;
                expectName = true;
                if (i >= path.Length)
                    return false;
            }
            else if (i < path.Length && path[i] == ']')
                return false;
        }
        return parts.Count > 0;
    }

    private static Result FromJson(string text, string path)
    {
        TryPath(path, out var parts, out _);
        var shown = Shorten(path);
        text = text.Trim().TrimStart('﻿').Trim();
        if (text.Length == 0)
            return new(null, "There is no JSON to read: the text is empty.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException)
        {
            return new(null, "The text isn't valid JSON (or nests deeper than 64 levels).");
        }
        using (document)
        {
            var at = document.RootElement;
            foreach (var part in parts)
            {
                if (part.Name is { } name)
                {
                    if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(name, out var inner))
                        return new(null, $"That JSON has nothing at \"{shown}\".");
                    at = inner;
                }
                else
                {
                    if (at.ValueKind != JsonValueKind.Array || part.Index >= at.GetArrayLength())
                        return new(null, $"That JSON has nothing at \"{shown}\".");
                    at = at[part.Index];
                }
            }
            return at.ValueKind switch
            {
                JsonValueKind.Null => new(null, $"\"{shown}\" is null, so there is nothing to take."),
                JsonValueKind.String => new(at.GetString() ?? "", null),
                _ => new(at.GetRawText(), null), // a number as written, true / false, or the object / array's own JSON
            };
        }
    }

    // ---- Pattern ----

    /// <summary>
    /// A pattern, interpreted (no <c>RegexOptions.Compiled</c>: nothing is generated at run time, so it is Native AOT clean) and
    /// with a time limit. Not <c>Multiline</c>: write <c>(?m)</c> for line starts and ends.
    /// </summary>
    public static bool TryPattern(string pattern, out Regex? regex, out string why)
    {
        regex = null;
        why = "";
        if (pattern.Length > MaxPattern)
        {
            why = $"The pattern is longer than {MaxPattern} characters.";
            return false;
        }
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            why = "That isn't a valid pattern. A pattern like (\\d+) takes the digits.";
            return false;
        }
    }

    private static Result FromPattern(string text, string pattern)
    {
        TryPattern(pattern, out var regex, out _);
        try
        {
            var match = regex!.Match(text);
            if (!match.Success)
                return new(null, "The pattern doesn't match the text.");
            var taken = match.Groups.Count > 1 ? match.Groups[1] : match.Groups[0];
            return taken.Success ? new(taken.Value, null) : new(null, "The pattern matches, but its first group has nothing in it.");
        }
        catch (RegexMatchTimeoutException)
        {
            return new(null, "The pattern took too long on this text. Try a simpler one.");
        }
    }

    // ---- Between two texts, and a line ----

    private static Result FromBetween(string text, string before, string after)
    {
        var start = 0;
        if (before.Length > 0)
        {
            var at = text.IndexOf(before, StringComparison.Ordinal);
            if (at < 0)
                return new(null, $"The text doesn't contain \"{Shorten(before)}\".");
            start = at + before.Length;
        }
        var end = text.Length;
        if (after.Length > 0)
        {
            var at = text.IndexOf(after, start, StringComparison.Ordinal);
            if (at < 0)
                return new(null, $"The text doesn't contain \"{Shorten(after)}\" after that.");
            end = at;
        }
        return new(text[start..end], null);
    }

    private static bool TryLine(string text, out int line) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out line) && line is >= 1 and <= MaxLine;

    private static Result FromLine(string text, string number)
    {
        TryLine(number, out var wanted);
        var count = 0;
        var start = 0;
        while (true)
        {
            count++;
            var end = text.IndexOf('\n', start);
            var stop = end < 0 ? text.Length : end;
            if (count == wanted)
                return new(text[start..stop].TrimEnd('\r'), null);
            if (end < 0)
                return new(null, $"The text has only {count} line{(count == 1 ? "" : "s")}.");
            start = end + 1;
        }
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>A plain number as <c>number</c> gives it (the same reading as Check value), else null.</summary>
    public static bool IsNumber(string? text) => text is not null && StepValues.TryNumber(text.Trim(), out _);

    /// <summary>The settings that fill references and may hold a Sensitive value: the output is Sensitive when any does (D-143).</summary>
    public static readonly string[] Sources = ["text", "find", "until"];

    /// <summary>The text with its <c>{{</c> escapes undone, for a setting written without any reference: what the run sees after filling.</summary>
    public static string AsFilled(string written) => string.Concat(StepValues.Split(written).Select(p => p.Text));
}
