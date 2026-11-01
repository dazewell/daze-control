using System.Globalization;
using DazeControl.Core;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;
using DazeControl.Ipc;

namespace DazeControl.Cli;

/// <summary>
/// <c>dctl</c> (FEATURES F8): talks to the running engine over the pipe (D-011); <c>validate &lt;file&gt;</c>, and
/// <c>list</c> / <c>validate</c> without an engine, read config.yaml themselves.
/// </summary>
public static class Program
{
    private const int Ok = 0;
    private const int Failed = 1;
    private const int Usage = 2;
    private const int NoEngine = 3;

    private const string Help = """
        Usage: dctl <command>
          run <flow> [--wait] [--dry-run]   start a flow (by id or name); --wait prints how it went
          cancel <flow>                     stop a running flow
          list                              flows and whether they can run
          validate [file]                   problems in config.yaml (or in the file given)
          status                            what the engine is doing
          history [flow] [--limit n]        recent runs, newest first
          autostart [on|off]                start the engine with Windows
          devices audio|bluetooth|displays|adb
        Exit codes: 0 ok, 1 failed, 2 usage, 3 the engine isn't running.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            Console.Out.WriteLine(Help);
            return args.Length == 0 ? Usage : Ok;
        }
        try
        {
            return await RunAsync(args[0], args[1..]).ConfigureAwait(false);
        }
        catch (UsageException e)
        {
            return UsageError(e.Message);
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Lost the connection to the engine: {e.Message}");
            return Failed;
        }
    }

    private static async Task<int> RunAsync(string command, string[] rest)
    {
        var flags = rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var words = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        switch (command)
        {
            case "run":
                if (words.Count != 1)
                    return UsageError("run needs one flow.");
                return await SendAsync(new PipeRequest
                {
                    Op = Ops.Run, Flow = words[0], Wait = flags.Contains("--wait") || flags.Contains("--dry-run"),
                    DryRun = flags.Contains("--dry-run"), Trigger = "Command line",
                }, PrintRun).ConfigureAwait(false);
            case "cancel":
                return words.Count == 1
                    ? await SendAsync(new PipeRequest { Op = Ops.Cancel, Flow = words[0] }, _ => Ok).ConfigureAwait(false)
                    : UsageError("cancel needs one flow.");
            case "list":
                return await SendAsync(new PipeRequest { Op = Ops.List }, PrintFlows, offline: ListOffline).ConfigureAwait(false);
            case "validate":
                if (words.Count == 1)
                    return ValidateOffline(words[0]);
                return await SendAsync(new PipeRequest { Op = Ops.Validate }, m => PrintProblems(m.Problems ?? []),
                    offline: () => ValidateOffline(DefaultConfigPath(), missingIsEmpty: true)).ConfigureAwait(false);
            case "status":
                return await SendAsync(new PipeRequest { Op = Ops.Status }, PrintStatus).ConfigureAwait(false);
            case "history":
                int? limit = null;
                var limitAt = rest.ToList().IndexOf("--limit");
                if (limitAt >= 0)
                {
                    if (limitAt + 1 >= rest.Length || !int.TryParse(rest[limitAt + 1], CultureInfo.InvariantCulture, out var n) || n < 1)
                        return UsageError("--limit needs a number.");
                    limit = n;
                    words.Remove(rest[limitAt + 1]);
                }
                return await SendAsync(new PipeRequest { Op = Ops.History, Flow = words.FirstOrDefault(), Limit = limit },
                    PrintHistory).ConfigureAwait(false);
            case "autostart":
                bool? enable = words.FirstOrDefault() switch
                {
                    null => null,
                    "on" => true,
                    "off" => false,
                    _ => throw new UsageException("autostart takes on or off."),
                };
                return await SendAsync(new PipeRequest { Op = Ops.Autostart, Enable = enable }, m =>
                {
                    Console.Out.WriteLine(m.Autostart!.State switch
                    {
                        "on" => $"Starts with Windows: {m.Autostart.Path}",
                        "disabledByWindows" => "Turned off in Task Manager → Startup apps.",
                        _ => "Doesn't start with Windows.",
                    });
                    return Ok;
                }).ConfigureAwait(false);
            case "devices":
                return await SendAsync(new PipeRequest { Op = Ops.Devices, Flow = words.FirstOrDefault() }, _ => Ok).ConfigureAwait(false);
            default:
                return UsageError($"Unknown command \"{command}\".");
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run dctl help for the commands.");
        return Usage;
    }

    private sealed class UsageException(string message) : Exception(message);

    /// <summary>Sends one request; prints the engine's error, or hands the answer to <paramref name="print"/>.</summary>
    private static async Task<int> SendAsync(PipeRequest request, Func<PipeMessage, int> print, Func<int>? offline = null)
    {
        await using var client = await PipeClient.ConnectAsync(Protocol.CurrentPipeName(), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (client is null)
        {
            if (offline is not null)
                return offline();
            Console.Error.WriteLine($"{AppInfo.ProductName} isn't running. Start it from the Start menu, then try again.");
            return NoEngine;
        }
        var answer = await client.SendAsync(request).ConfigureAwait(false);
        if (!answer.Ok)
        {
            Console.Error.WriteLine(answer.Error);
            return Failed;
        }
        return print(answer);
    }

    private static int PrintRun(PipeMessage message)
    {
        if (message.Run is not { } run)
        {
            Console.Out.WriteLine("Started.");
            return Ok;
        }
        Console.Out.WriteLine($"{run.FlowName}: {Describe(run.Outcome)} ({Seconds(run.Duration)})");
        foreach (var node in run.Nodes)
        {
            var error = node.Error is null ? "" : $" — {node.Error}";
            var attempts = node.Attempts > 1 ? $", {node.Attempts} tries" : "";
            Console.Out.WriteLine($"  {node.NodeId}: {node.Outcome}{attempts}{error}");
        }
        return run.Outcome is FlowOutcome.Succeeded ? Ok : Failed;
    }

    private static int PrintFlows(PipeMessage message)
    {
        if (message.Flows is not { Length: > 0 })
            Console.Out.WriteLine("No flows yet.");
        foreach (var flow in message.Flows ?? [])
        {
            var state = !flow.Enabled ? "off" : !flow.Runnable ? "needs fixing" : flow.Stale ? "runs its last good version"
                : flow.Running ? "running" : "ready";
            var hotkeys = flow.Hotkeys is { Length: > 0 } h ? $"  [{string.Join(", ", h)}]" : "";
            Console.Out.WriteLine($"{flow.Id,-24} {flow.Name}{hotkeys} — {state}");
        }
        return Ok;
    }

    private static int PrintStatus(PipeMessage message)
    {
        var s = message.Status!;
        Console.Out.WriteLine($"{AppInfo.ProductName} {s.Version} (process {s.ProcessId})");
        Console.Out.WriteLine($"Config: {s.ConfigPath} — {s.Errors} error(s), {s.Warnings} warning(s)");
        Console.Out.WriteLine(s.Running is { Length: > 0 } running ? $"Running: {string.Join(", ", running)}" : "Nothing running.");
        if (s.ShortcutsPaused)
            Console.Out.WriteLine("Shortcuts are paused.");
        foreach (var problem in s.ShortcutProblems ?? [])
            Console.Out.WriteLine($"Shortcut: {problem}");
        return Ok;
    }

    private static int PrintHistory(PipeMessage message)
    {
        foreach (var run in message.History ?? [])
        {
            var when = run.Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Console.Out.WriteLine($"{when}  {run.FlowName} ({run.Trigger}): {Describe(run.Outcome)}, {Seconds(run.Duration)}");
        }
        return Ok;
    }

    private static int PrintProblems(IEnumerable<ProblemInfo> problems)
    {
        var list = problems.ToList();
        foreach (var p in list)
            Console.Out.WriteLine($"{p.Severity}: {p.Path}: {p.Message}");
        Console.Out.WriteLine(list.Count == 0 ? "No problems." : $"{list.Count} problem(s).");
        return list.Any(p => p.Severity == "error") ? Failed : Ok;
    }

    private static string Describe(FlowOutcome outcome) => outcome switch
    {
        FlowOutcome.Succeeded => "done",
        FlowOutcome.PartiallySucceeded => "done, with a problem",
        FlowOutcome.Cancelled => "stopped",
        _ => "didn't finish",
    };

    private static string Seconds(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0} s");

    private static string DefaultConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName, "config.yaml");

    /// <param name="missingIsEmpty">The default config.yaml: missing means no flows yet, as in the engine.</param>
    private static (Configuration? Config, List<Problem> Problems)? Load(string path, bool missingIsEmpty)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (missingIsEmpty && e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (Configuration.Empty, []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Can't read {path}: {e.Message}");
            return null;
        }
        var load = ConfigYaml.Load(text);
        var problems = load.Problems.ToList();
        if (load.Config is { } config)
            problems.AddRange(ConfigValidator.Validate(config, load.Problems));
        return (load.Config, problems);
    }

    private static int ValidateOffline(string path, bool missingIsEmpty = false)
    {
        if (Load(path, missingIsEmpty) is not var (_, problems))
            return Failed;
        return PrintProblems(problems.Select(p => new ProblemInfo
        {
            Severity = p.Severity == Severity.Error ? "error" : "warning", Path = p.Path, Message = p.Message, FlowId = p.FlowId,
        }));
    }

    private static int ListOffline()
    {
        var path = DefaultConfigPath();
        if (Load(path, missingIsEmpty: true) is not ({ } config, var problems))
            return Failed;
        Console.Out.WriteLine($"({AppInfo.ProductName} isn't running; read {path})");
        var broken = ConfigValidator.FlowsWithErrors(problems);
        return PrintFlows(new PipeMessage
        {
            Flows = [.. config.Flows.Select(f => new FlowInfo
            {
                Id = f.Id, Name = f.Name, Enabled = f.Enabled, Runnable = f.Enabled && !broken.Contains(f.Id), Hotkeys = [.. f.Hotkeys],
            })],
        });
    }
}
