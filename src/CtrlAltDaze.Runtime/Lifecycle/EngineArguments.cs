using System.Text;

namespace CtrlAltDaze.Runtime.Lifecycle;

/// <summary>
/// The engine's command line: <c>CtrlAltDaze.exe [--run &lt;flow id&gt;] [config.yaml]</c>. <c>--run</c> comes from a
/// desktop shortcut (D-049); the config path is the one a non-default engine gave the shortcut (as autostart does).
/// Velopack's hook arguments never get here: its hooks exit first.
/// </summary>
public sealed record EngineArguments(string? Config, string? RunFlowId)
{
    public const string RunFlag = "--run";

    /// <summary>
    /// <c>--run &lt;flow&gt; name=value…</c> (T24.9): the flow's inputs, as <c>cad run</c> takes them; null when none. After
    /// <c>--run &lt;flow&gt;</c> a word that is <c>name=…</c> with a valid input name is an input, never the config: a config
    /// path of that shape goes before <c>--run</c> (<see cref="Format"/> does).
    /// </summary>
    public IReadOnlyDictionary<string, string>? RunInputs { get; init; }

    public static EngineArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? config = null, run = null;
        Dictionary<string, string>? inputs = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == RunFlag)
            {
                // "--run" with nothing after it runs nothing; an empty id is no id.
                if (i + 1 < args.Count && args[i + 1].Length > 0)
                    run ??= args[i + 1];
                i++;
            }
            else if (run is not null && IsInput(args[i]))
            {
                // A repeated name: the first wins, like a repeated --run; the engine checks names against the flow.
                var equals = args[i].IndexOf('=', StringComparison.Ordinal);
                (inputs ??= new(StringComparer.Ordinal)).TryAdd(args[i][..equals], args[i][(equals + 1)..]);
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && args[i].Length > 0)
            {
                config ??= args[i];
            }
        }
        return new EngineArguments(config, run) { RunInputs = inputs };
    }

    /// <summary>An input name is lower-case letters, digits and "-" starting with a letter (<c>ConfigValidator</c>'s rule).</summary>
    private static bool IsInput(string arg) =>
        arg.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals && char.IsAsciiLetterLower(arg[0])
        && arg.AsSpan(0, equals).IndexOfAnyExcept(InputNameChars) < 0;

    private static readonly System.Buffers.SearchValues<char> InputNameChars = System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    /// <summary>
    /// A shortcut's arguments: the config path when there is one, then the real flow id. The config goes first because
    /// words after <c>--run &lt;flow&gt;</c> that look like <c>name=value</c> are inputs (T24.9), so a config path like
    /// <c>settings=prod.yaml</c> must never sit there.
    /// </summary>
    public static string Format(string flowId, string? config)
    {
        ArgumentException.ThrowIfNullOrEmpty(flowId);
        var text = $"{RunFlag} {Quote(flowId)}";
        return config is null ? text : $"{Quote(config)} {text}";
    }

    /// <summary>
    /// Windows' rules for one argument (CommandLineToArgvW): always quoted, so an id that starts with "--" or holds
    /// spaces stays one word; backslashes are doubled only before a quote.
    /// </summary>
    internal static string Quote(string arg)
    {
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            text.Append('\\', c == '"' ? 2 * slashes + 1 : slashes);
            slashes = 0;
            text.Append(c);
        }
        text.Append('\\', 2 * slashes).Append('"');
        return text.ToString();
    }

    /// <summary>Splits a shortcut's arguments the way the C runtime does for <c>Main(string[])</c>.</summary>
    internal static List<string> Split(string commandLine)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool quoted = false, any = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var count = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    count++;
                    i++;
                }
                var beforeQuote = i < commandLine.Length && commandLine[i] == '"';
                word.Append('\\', beforeQuote ? count / 2 : count);
                if (beforeQuote && count % 2 == 1)
                {
                    word.Append('"'); // an escaped quote
                    i++;
                }
                any = true;
            }
            else if (c == '"')
            {
                // "" inside quotes is a literal quote (msvcrt since 2008).
                if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    word.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
                any = true;
                i++;
            }
            else if (!quoted && c is ' ' or '\t')
            {
                if (any)
                    words.Add(word.ToString());
                word.Clear();
                any = false;
                i++;
            }
            else
            {
                word.Append(c);
                any = true;
                i++;
            }
        }
        if (any)
            words.Add(word.ToString());
        return words;
    }
}
