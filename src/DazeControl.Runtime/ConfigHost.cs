using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Runtime;

/// <param name="FlowId">The flow's YAML key.</param>
/// <param name="Running">The version triggers run: the file's when valid, else the last valid one (D-021) minus
/// any hotkey a current flow took; null = never valid, disabled.</param>
/// <param name="Problems">Problems of the version in the file now.</param>
public sealed record FlowState(string FlowId, Flow? Running, ValueList<Problem> Problems)
{
    /// <summary>The library <see cref="Running"/> was validated with: hand it to the run, not the snapshot's.</summary>
    public Library Library { get; init; } = Library.Empty;

    /// <summary>The last valid version as written, so a hotkey it yielded comes back once it's free again.</summary>
    public Flow? LastValid { get; init; }

    /// <summary>The version in the file now, errors and all (the tray names a flow that needs fixing by it).</summary>
    public Flow? Written { get; init; }

    /// <summary>The file's version has errors but an older valid one still runs (the editor says so).</summary>
    public bool IsStale => Running is not null && Problems.Any(p => p.Severity == Severity.Error);
}

/// <param name="Settings">From the last readable file.</param>
/// <param name="Library">From the last readable file. A stale flow runs with <see cref="FlowState.Library"/>.</param>
/// <param name="Flows">In file order.</param>
/// <param name="Problems">Everything wrong with the file now: reading, mapping and validation.</param>
public sealed record ConfigSnapshot(Settings Settings, Library Library, ValueList<FlowState> Flows, ValueList<Problem> Problems)
{
    public static ConfigSnapshot Empty { get; } = new(new Settings(), Library.Empty, [], []);

    /// <summary>What hotkeys, the tray and the CLI may run: a valid version that is enabled.</summary>
    public IEnumerable<Flow> RunnableFlows => Flows.Select(f => f.Running).OfType<Flow>().Where(f => f.Enabled);
}

/// <summary>Raises <see cref="Changed"/> when the watched file may have changed. Abstracted for tests.</summary>
public interface IFileChangeSource : IDisposable
{
    event Action? Changed;
}

/// <summary>
/// Owns the engine's view of config.yaml (ARCHITECTURE → Config): loads and validates it, keeps the last valid
/// version of each flow (D-021), and reloads after the file stops changing for <see cref="Debounce"/> (editor
/// autosave writes in bursts). A file that can't be read or parsed changes nothing but the problems. A missing
/// file is an empty configuration (first start).
/// </summary>
public sealed class ConfigHost : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly IFileChangeSource? _changes;
    private readonly ITimer _debounce;
    private readonly Problem? _watchProblem;

    /// <param name="path">config.yaml.</param>
    /// <param name="time">Clock for the debounce.</param>
    /// <param name="changes">File change notifications; null = <see cref="FileWatcher"/> on <paramref name="path"/>.</param>
    public ConfigHost(string path, TimeProvider time, IFileChangeSource? changes = null)
    {
        _path = path;
        _debounce = time.CreateTimer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            _changes = changes ?? new FileWatcher(path);
            _changes.Changed += () => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fail-soft: load once without watching; the editor shows why edits need a restart.
            _watchProblem = new Problem(Severity.Warning, "", $"Can't watch the file for changes: {e.Message}");
        }
        Reload();
    }

    public ConfigSnapshot Current { get; private set; } = ConfigSnapshot.Empty;

    /// <summary>
    /// Raised after every reload, on a pool thread (or the constructor's), in reload order: it's raised under the
    /// reload lock, so a handler must not wait for another thread's <see cref="Reload"/>.
    /// </summary>
    public event Action<ConfigSnapshot>? Changed;

    public void Reload()
    {
        lock (_lock)
        {
            var next = Apply(Current, Read(_path));
            if (_watchProblem is not null)
                next = next with { Problems = [.. next.Problems, _watchProblem] };
            Current = next;
            foreach (var listener in Changed?.GetInvocationList().Cast<Action<ConfigSnapshot>>() ?? [])
            {
                try
                {
                    listener(next);
                }
#pragma warning disable CA1031 // fail-soft: a broken listener must not stop reloads or the timer thread
                catch (Exception e)
#pragma warning restore CA1031
                {
                    ListenerFailed?.Invoke(e);
                }
            }
        }
    }

    /// <summary>A <see cref="Changed"/> handler threw (for the log); the other handlers still ran.</summary>
    public event Action<Exception>? ListenerFailed;

    public void Dispose()
    {
        _changes?.Dispose();
        _debounce.Dispose();
    }

    private static ConfigLoad Read(string path)
    {
        try
        {
            return ConfigYaml.Load(File.ReadAllText(path));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ConfigLoad(Configuration.Empty, []); // first start
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ConfigLoad(null, [new Problem(Severity.Error, "", $"Can't read the file: {e.Message}")]);
        }
    }

    /// <summary>
    /// The D-021 rule: per flow, the new version if valid, else the previous last valid one, else none. A retained
    /// version keeps the library it was validated with and runs without any hotkey a current flow has taken.
    /// </summary>
    internal static ConfigSnapshot Apply(ConfigSnapshot previous, ConfigLoad load)
    {
        if (load.Config is not { } config)
            return previous with { Problems = load.Problems };
        var problems = load.Problems.Concat(ConfigValidator.Validate(config, load.Problems)).ToList();
        var rejected = ConfigValidator.FlowsWithErrors(problems);

        // Hotkeys of current flows were checked against each other; a retained version yields to them.
        var taken = config.Flows.Where(f => !rejected.Contains(f.Id) && f.Enabled)
            .SelectMany(f => f.Hotkeys).Select(ParseHotkey).OfType<Hotkey>().ToHashSet();
        var flows = new List<FlowState>();
        foreach (var flow in config.Flows)
        {
            if (!rejected.Contains(flow.Id))
            {
                flows.Add(new FlowState(flow.Id, flow, []) { Library = config.Library, LastValid = flow });
                continue;
            }
            var old = previous.Flows.FirstOrDefault(f => f.FlowId == flow.Id);
            if (old?.LastValid is not { } valid)
            {
                flows.Add(new FlowState(flow.Id, null, []));
                continue;
            }
            var running = valid;
            if (valid.Enabled)
            {
                var keep = valid.Hotkeys.Where(text => ParseHotkey(text) is not { } hotkey || taken.Add(hotkey)).ToList();
                if (keep.Count < valid.Hotkeys.Count)
                {
                    running = valid with { Hotkeys = new(keep) };
                    problems.Add(new Problem(Severity.Warning, $"flows.{flow.Id}.hotkeys",
                        $"The last valid version of this flow lost {string.Join(", ", valid.Hotkeys.Except(keep))} to another flow.", flow.Id));
                }
            }
            flows.Add(new FlowState(flow.Id, running, []) { Library = old.Library, LastValid = valid });
        }
        return new ConfigSnapshot(config.Settings, config.Library,
            new(flows.Select((f, i) => f with { Written = config.Flows[i], Problems = new(problems.Where(p => p.FlowId == f.FlowId)) })),
            new(problems));
    }

    private static Hotkey? ParseHotkey(string text) => Hotkey.TryParse(text, out var hotkey) ? hotkey : null;

    /// <summary>FileSystemWatcher on the file's folder (editors replace the file, so watch renames too).</summary>
    private sealed class FileWatcher : IFileChangeSource
    {
        private readonly FileSystemWatcher? _watcher;

        public FileWatcher(string path)
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full)!;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => Changed?.Invoke();
            _watcher.Created += (_, _) => Changed?.Invoke();
            _watcher.Deleted += (_, _) => Changed?.Invoke();
            _watcher.Renamed += (_, _) => Changed?.Invoke();
            try
            {
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                _watcher.Dispose();
                throw;
            }
        }

        public event Action? Changed;

        public void Dispose() => _watcher?.Dispose();
    }
}
