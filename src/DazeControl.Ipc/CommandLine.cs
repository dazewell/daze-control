using System.Globalization;

namespace DazeControl.Ipc;

/// <summary>
/// A <c>dctl</c> command line, checked strictly: an unknown flag or a wrong number of words is a usage error,
/// never ignored — <c>run tv --dryrun</c> must not start the real flow.
/// </summary>
public sealed record CommandLine(string Command, IReadOnlyList<string> Words, bool Wait, bool DryRun, int? Limit)
{
    /// <summary>shortcut: take the flow's desktop shortcut away instead of adding it.</summary>
    public bool Remove { get; init; }

    /// <summary>backup / restore: the file that stands in for the default config.yaml (tests, a copy of the app).</summary>
    public string? Config { get; init; }
    public bool Merge { get; init; }
    public bool Replace { get; init; }
    public bool Yes { get; init; }
    public bool Editor { get; init; }
    public bool History { get; init; }

    /// <summary>update: <c>stable</c> or <c>rc</c> (lower-case), the channel to switch to first (D-108).</summary>
    public string? Channel { get; init; }

    /// <summary>run: the flow's inputs given as <c>name=value</c> words after the flow (T24.9); null when none.</summary>
    public IReadOnlyDictionary<string, string>? Inputs { get; init; }

    /// <summary>Most <c>name=value</c> words a run takes (a flow has at most a handful of inputs).</summary>
    public const int MaxInputs = 32;

    /// <summary>
    /// <c>name=value</c> words as a map (T24.9): the value is everything after the first "=", empty allowed, never interpreted.
    /// Which names exist and what values fit is the engine's to check against the flow.
    /// </summary>
    /// <exception cref="CommandLineException">A word without "name=", or a name given twice.</exception>
    public static Dictionary<string, string> ParseInputs(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            var equals = word.IndexOf('=', StringComparison.Ordinal);
            if (equals < 1)
                throw new CommandLineException("Inputs after the flow are written name=value (a value is never shown here)."); // not echoed: it may be a secret (D-014)
            if (!inputs.TryAdd(word[..equals], word[(equals + 1)..]))
                throw new CommandLineException($"Input \"{word[..equals]}\" is given twice.");
        }
        return inputs;
    }

    private sealed record Shape(int MinWords, int MaxWords, string WordsError, params string[] Flags);

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.Ordinal)
    {
        ["run"] = new(1, 1 + MaxInputs, $"run needs one flow, then at most {MaxInputs} inputs as name=value.", "--wait", "--dry-run"),
        ["cancel"] = new(1, 1, "cancel needs one flow."),
        ["list"] = new(0, 0, "list takes no arguments."),
        ["validate"] = new(0, 1, "validate takes at most one file."),
        ["status"] = new(0, 0, "status takes no arguments."),
        ["history"] = new(0, 1, "history takes at most one flow.", "--limit"),
        ["autostart"] = new(0, 1, "autostart takes on or off."),
        ["devices"] = new(1, 1, "devices needs one kind: audio, bluetooth, displays, adb, services, credentials, wifi or gamepads."),
        ["capture-display-layout"] = new(1, 1, "capture-display-layout needs a name for the layout."),
        ["update"] = new(0, 0, "update takes no arguments but --channel stable|rc.", "--channel"),
        ["backup"] = new(0, 1, "backup takes at most one file.", "--config", "--editor", "--history"),
        ["restore"] = new(1, 1, "restore needs one backup file.", "--config", "--merge", "--replace", "--yes", "--dry-run", "--editor"),
        ["shortcut"] = new(1, 1, "shortcut needs one flow.", "--remove"),
        ["check"] = new(1, 16, "check needs a check, like network.wifi, then its settings as name=value."),
    };

    /// <exception cref="CommandLineException">The command, a flag or the words don't fit.</exception>
    public static CommandLine Parse(string command, IReadOnlyList<string> rest)
    {
        if (!Shapes.TryGetValue(command, out var shape))
            throw new CommandLineException($"Unknown command \"{command}\".");
        var words = new List<string>();
        bool wait = false, dryRun = false, remove = false;
        int? limit = null;
        string? config = null, channel = null;
        bool merge = false, replace = false, yes = false, editor = false, history = false;
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
                case "--remove":
                    remove = true;
                    break;
                case "--merge":
                    merge = true;
                    break;
                case "--replace":
                    replace = true;
                    break;
                case "--yes":
                    yes = true;
                    break;
                case "--editor":
                    editor = true;
                    break;
                case "--history":
                    history = true;
                    break;
                case "--config":
                    if (config is not null || i + 1 >= rest.Count || rest[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new CommandLineException("--config needs one file.");
                    config = rest[++i];
                    break;
                case "--channel":
                    if (channel is not null || i + 1 >= rest.Count || rest[i + 1].ToLowerInvariant() is not ("stable" or "rc"))
                        throw new CommandLineException("--channel needs stable or rc.");
                    channel = rest[i + 1].ToLowerInvariant();
                    i++;
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
        if (merge && replace)
            throw new CommandLineException("Choose --merge or --replace, not both.");
        return new CommandLine(command, words, wait, dryRun, limit)
        {
            Inputs = command == "run" && words.Count > 1 ? ParseInputs(words.Skip(1)) : null,
            Remove = remove, Config = config, Merge = merge, Replace = replace, Yes = yes, Editor = editor, History = history, Channel = channel,
        };
    }

    /// <summary>The first word, if any.</summary>
    public string? Word => Words.Count > 0 ? Words[0] : null;
}

public sealed class CommandLineException(string message) : Exception(message);
