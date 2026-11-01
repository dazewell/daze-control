using System.Globalization;

namespace DazeControl.Ipc;

/// <summary>
/// A <c>dctl</c> command line, checked strictly: an unknown flag or a wrong number of words is a usage error,
/// never ignored — <c>run tv --dryrun</c> must not start the real flow.
/// </summary>
public sealed record CommandLine(string Command, IReadOnlyList<string> Words, bool Wait, bool DryRun, int? Limit)
{
    private sealed record Shape(int MinWords, int MaxWords, string WordsError, params string[] Flags);

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.Ordinal)
    {
        ["run"] = new(1, 1, "run needs one flow.", "--wait", "--dry-run"),
        ["cancel"] = new(1, 1, "cancel needs one flow."),
        ["list"] = new(0, 0, "list takes no arguments."),
        ["validate"] = new(0, 1, "validate takes at most one file."),
        ["status"] = new(0, 0, "status takes no arguments."),
        ["history"] = new(0, 1, "history takes at most one flow.", "--limit"),
        ["autostart"] = new(0, 1, "autostart takes on or off."),
        ["devices"] = new(1, 1, "devices needs one kind: audio, bluetooth, displays or adb."),
        ["capture-display-layout"] = new(1, 1, "capture-display-layout needs a name for the layout."),
    };

    /// <exception cref="CommandLineException">The command, a flag or the words don't fit.</exception>
    public static CommandLine Parse(string command, IReadOnlyList<string> rest)
    {
        if (!Shapes.TryGetValue(command, out var shape))
            throw new CommandLineException($"Unknown command \"{command}\".");
        var words = new List<string>();
        bool wait = false, dryRun = false;
        int? limit = null;
        for (var i = 0; i < rest.Count; i++)
        {
            var arg = rest[i];
            if (!arg.StartsWith('-'))
            {
                words.Add(arg);
                continue;
            }
            if (!shape.Flags.Contains(arg, StringComparer.Ordinal))
                throw new CommandLineException($"{command} doesn't know {arg}.");
            switch (arg)
            {
                case "--wait":
                    wait = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--limit":
                    if (limit is not null || i + 1 >= rest.Count
                        || !int.TryParse(rest[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                        throw new CommandLineException("--limit needs one number, 1 or more.");
                    limit = n;
                    break;
            }
        }
        if (words.Count < shape.MinWords || words.Count > shape.MaxWords)
            throw new CommandLineException(shape.WordsError);
        return new CommandLine(command, words, wait, dryRun, limit);
    }

    /// <summary>The first word, if any.</summary>
    public string? Word => Words.Count > 0 ? Words[0] : null;
}

public sealed class CommandLineException(string message) : Exception(message);
