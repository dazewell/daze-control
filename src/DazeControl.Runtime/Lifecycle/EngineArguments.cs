using System.Text;

namespace DazeControl.Runtime.Lifecycle;

/// <summary>
/// The engine's command line: <c>DazeControl.exe [--run &lt;flow id&gt;] [config.yaml]</c>. <c>--run</c> comes from a
/// desktop shortcut (D-049); the config path is the one a non-default engine gave the shortcut (as autostart does).
/// Velopack's hook arguments never get here: its hooks exit first.
/// </summary>
public sealed record EngineArguments(string? Config, string? RunFlowId)
{
    public const string RunFlag = "--run";

    public static EngineArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? config = null, run = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == RunFlag)
            {
                // "--run" with nothing after it runs nothing; an empty id is no id.
                if (i + 1 < args.Count && args[i + 1].Length > 0)
                    run ??= args[i + 1];
                i++;
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && args[i].Length > 0)
            {
                config ??= args[i];
            }
        }
        return new EngineArguments(config, run);
    }

    /// <summary>A shortcut's arguments: the real flow id, then the config path when there is one.</summary>
    public static string Format(string flowId, string? config)
    {
        ArgumentException.ThrowIfNullOrEmpty(flowId);
        var text = $"{RunFlag} {Quote(flowId)}";
        return config is null ? text : $"{text} {Quote(config)}";
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
