using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DazeControl.Core;
using DazeControl.Core.Backup;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Updates;
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
          devices audio|bluetooth|displays|adb|services|credentials|wifi
                                            what steps can name on this PC
          capture-display-layout <name>     the screens as they are now, as YAML for library.displays
          shortcut <flow> [--remove]        put a desktop shortcut that runs the flow (or take it away)
          backup [file] [--editor] [--history] [--config <path>]
                                            save config.yaml (and the editor layout / run history if asked) to a .dcbackup file
          restore <file> [--merge|--replace] [--yes] [--dry-run] [--editor] [--config <path>]
                                            --dry-run shows what would change; --yes applies it (--replace is the default)
          check <check> [name=value...]     read one fact now, like: check network.wifi network=Home (0 = yes, 1 = no)
          update [--channel stable|rc]      download a newer version (release candidates with rc); it installs at the next start
        Exit codes: 0 ok, 1 failed, 2 usage (restore: also refused without --yes), 3 the engine isn't running.
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
        CommandLine line;
        try
        {
            line = CommandLine.Parse(command, rest);
        }
        catch (CommandLineException e)
        {
            return UsageError(e.Message);
        }
        switch (command)
        {
            case "run":
                return await SendAsync(new PipeRequest
                {
                    Op = Ops.Run, Flow = line.Word, Wait = line.Wait || line.DryRun, DryRun = line.DryRun, Trigger = "Command line",
                }, PrintRun).ConfigureAwait(false);
            case "cancel":
                return await SendAsync(new PipeRequest { Op = Ops.Cancel, Flow = line.Word }, _ => Ok).ConfigureAwait(false);
            case "list":
                return await SendAsync(new PipeRequest { Op = Ops.List }, PrintFlows, offline: ListOffline).ConfigureAwait(false);
            case "validate":
                if (line.Word is { } file)
                    return ValidateOffline(file);
                return await SendAsync(new PipeRequest { Op = Ops.Validate }, m => PrintProblems(m.Problems ?? []),
                    offline: () => ValidateOffline(DefaultConfigPath(), missingIsEmpty: true)).ConfigureAwait(false);
            case "status":
                return await SendAsync(new PipeRequest { Op = Ops.Status }, PrintStatus).ConfigureAwait(false);
            case "history":
                return await SendAsync(new PipeRequest { Op = Ops.History, Flow = line.Word, Limit = line.Limit },
                    PrintHistory).ConfigureAwait(false);
            case "autostart":
                bool? enable = line.Word switch
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
                return await SendAsync(new PipeRequest { Op = Ops.Devices, Kind = line.Word }, PrintDevices).ConfigureAwait(false);
            case "capture-display-layout":
                return await SendAsync(new PipeRequest { Op = Ops.CaptureDisplayLayout, Key = line.Word }, m =>
                {
                    Console.Out.Write(m.Yaml);
                    return Ok;
                }).ConfigureAwait(false);
            case "shortcut":
                return await SendAsync(new PipeRequest { Op = line.Remove ? Ops.RemoveShortcut : Ops.CreateShortcut, Flow = line.Word },
                    m => PrintShortcut(m, line.Remove)).ConfigureAwait(false);
            case "backup":
                return Commands(line).Backup(line.Word, line.Editor, line.History);
            case "restore":
                return Commands(line).Restore(line.Word!, line.Merge ? RestoreMode.AddMissing : RestoreMode.Replace, line.Yes, line.DryRun, line.Editor);
            case "update":
                return await UpdateAsync(line.Channel).ConfigureAwait(false);
            case "check":
                return await CheckAsync(line.Words).ConfigureAwait(false);
            default:
                throw new UnreachableException();
        }
    }

    private static BackupCommands Commands(CommandLine line)
    {
        var files = new PhysicalBackupFiles();
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
        var service = new BackupService(files, TimeProvider.System, version, Environment.MachineName);
        return new BackupCommands(service, files, BackupPaths.For(line.Config), Console.Out, Console.Error,
            interactive: !Console.IsInputRedirected && !Console.IsOutputRedirected, EditorRunning(line.Config));
    }

    // The session-local mutex the editor holds (Editor/Program.cs): one editor per user and config file.
    private static Func<bool> EditorRunning(string? config) => () =>
    {
        var scope = config is null || SamePath(config, BackupPaths.For(null).Config) ? "" : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(config).ToUpperInvariant())))[..16];
        if (!Mutex.TryOpenExisting(@"Local\DazeControl.Editor" + scope, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    };

    // The editor started without --config holds the unsuffixed mutex, also when the path given is the default one.
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static int PrintShortcut(PipeMessage message, bool removed)
    {
        var shortcuts = message.Shortcuts ?? [];
        if (removed)
            Console.Out.WriteLine(shortcuts.Length == 0 ? "It had no desktop shortcut." : $"Removed {string.Join(", ", shortcuts.Select(s => s.Path))}");
        else
            Console.Out.WriteLine($"On your desktop: {shortcuts.FirstOrDefault()?.Path}");
        return Ok;
    }

    /// <summary>Checks GitHub Releases and downloads a newer release; the engine applies it at its next start (D-043).</summary>
    private static async Task<int> UpdateAsync(string? channelWord)
    {
        try
        {
            // --channel sets the per-user choice the editor and the engine's daily check share (D-108).
            var store = new UpdateChannelStore(UpdateChannelStore.DefaultPath);
            if (UpdateChannels.FromWord(channelWord) is { } chosen)
            {
                try { store.Save(chosen); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Couldn't save the update channel: {e.Message}");
                    return Failed;
                }
            }
            var channel = store.Load();
            Console.Out.WriteLine($"Update channel: {channel.Label()}.");
            var updates = new VelopackUpdateClient(channel);
            if (!updates.IsInstalled)
            {
                Console.Error.WriteLine("This copy isn't installed, so it doesn't update. Install it with Setup.exe from GitHub Releases.");
                return Failed;
            }
            if (await updates.FindAsync(CancellationToken.None).ConfigureAwait(false) is not { } found)
            {
                Console.Out.WriteLine(updates.PendingVersion is { } pending
                    ? $"Version {pending} is downloaded and installs the next time {AppInfo.ProductName} starts."
                    : $"Up to date ({updates.CurrentVersion}).");
                return Ok;
            }
            TakeBeforeUpdateBackup();
            await updates.DownloadAsync(null, CancellationToken.None).ConfigureAwait(false);
            Console.Out.WriteLine($"Downloaded version {found}; it installs the next time {AppInfo.ProductName} starts.");
            return Ok;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Network, GitHub's rate limit, disk, a broken install: the engine's next daily check tries again.
            Console.Error.WriteLine($"Couldn't check for updates: {e.Message}");
            return Failed;
        }
    }

    /// <summary>A copy of the config before a new version is fetched (D-103). Fail-soft: the update goes on whatever happens here.</summary>
    private static void TakeBeforeUpdateBackup()
    {
        try
        {
            var files = new PhysicalBackupFiles();
            var paths = BackupPaths.For(null);
            var service = new BackupService(files, TimeProvider.System, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?", Environment.MachineName);
            var auto = new AutoBackup(service, files, TimeProvider.System, paths.Config, paths.AutoFolder, m => Console.Error.WriteLine(m));
            if (auto.Run(BackupService.BeforeUpdate, gated: false) == AutoBackupOutcome.Created)
                Console.Out.WriteLine("Saved a backup of your flows first.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Couldn't take the automatic backup ({e.Message}); updating anyway.");
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

    private static int PrintDevices(PipeMessage message)
    {
        if (message.Devices is not { Length: > 0 })
            Console.Out.WriteLine("None found.");
        foreach (var device in message.Devices ?? [])
        {
            // "D9:E0:8E:C2:96:85 — MX Master 3S, connected, can't connect from here"
            string?[] parts = [device.Detail, device.State, device.NotConnectable ? "can't connect from here" : null];
            var detail = string.Join(", ", parts.OfType<string>());
            Console.Out.WriteLine(detail.Length > 0 ? $"{device.Name} — {detail}" : device.Name);
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

    /// <summary>
    /// <c>dctl check &lt;check&gt; [name=value...]</c> (D-118): runs one check step through the engine's "Test this step",
    /// with the Library of config.yaml (so audio and Bluetooth checks can name its devices). Prints the check's
    /// sentence; exit 0 when the fact is as asked, 1 when not, so scripts can branch on it.
    /// </summary>
    private static async Task<int> CheckAsync(IReadOnlyList<string> words)
    {
        var path = DefaultConfigPath();
        var (request, error) = CheckCommand.Request(words, File.Exists(path) ? File.ReadAllText(path) : null);
        if (request is null)
        {
            Console.Error.WriteLine(error);
            return Usage;
        }
        return await SendAsync(request, PrintCheck).ConfigureAwait(false);
    }

    private static int PrintCheck(PipeMessage message)
    {
        if (message.Run?.Nodes is not [var node, ..])
            return Failed;
        if (node.Outcome == NodeOutcome.Succeeded)
        {
            Console.Out.WriteLine(node.Summary ?? "Yes");
            return Ok;
        }
        Console.Out.WriteLine(node.Error ?? node.Outcome.ToString());
        return Failed;
    }

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
