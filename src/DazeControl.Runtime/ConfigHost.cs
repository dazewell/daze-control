using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Runtime;

/// <param name="FlowId">The flow's YAML key.</param>
/// <param name="Running">The version triggers run: the file's when valid, else the last valid one (D-021); null =
/// never valid, disabled.</param>
/// <param name="Problems">Problems of the version in the file now.</param>
public sealed record FlowState(string FlowId, Flow? Running, ValueList<Problem> Problems)
{
    /// <summary>The file's version has errors but an older valid one still runs (the editor says so).</summary>
    public bool IsStale => Running is not null && Problems.Any(p => p.Severity == Severity.Error);
}

/// <param name="Settings">From the last readable file.</param>
/// <param name="Library">From the last readable file.</param>
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

    /// <param name="path">config.yaml.</param>
    /// <param name="time">Clock for the debounce.</param>
    /// <param name="changes">File change notifications; null = <see cref="FileWatcher"/> on <paramref name="path"/>.</param>
    public ConfigHost(string path, TimeProvider time, IFileChangeSource? changes = null)
    {
        _path = path;
        _debounce = time.CreateTimer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _changes = changes ?? new FileWatcher(path);
        _changes.Changed += () => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        Reload();
    }

    public ConfigSnapshot Current { get; private set; } = ConfigSnapshot.Empty;

    /// <summary>Raised after every reload, on a pool thread (or the constructor's).</summary>
    public event Action<ConfigSnapshot>? Changed;

    public void Reload()
    {
        ConfigSnapshot next;
        lock (_lock)
        {
            next = Apply(Current, Read(_path));
            Current = next;
        }
        Changed?.Invoke(next);
    }

    public void Dispose()
    {
        _changes?.Dispose();
        _debounce.Dispose();
    }

    private static ConfigLoad Read(string path)
    {
        try
        {
            return File.Exists(path) ? ConfigYaml.Load(File.ReadAllText(path)) : new ConfigLoad(Configuration.Empty, []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ConfigLoad(null, [new Problem(Severity.Error, "", $"Can't read the file: {e.Message}")]);
        }
    }

    /// <summary>The D-021 rule: per flow, the new version if valid, else the previous running one, else none.</summary>
    internal static ConfigSnapshot Apply(ConfigSnapshot previous, ConfigLoad load)
    {
        if (load.Config is not { } config)
            return previous with { Problems = load.Problems };
        var problems = load.Problems.Concat(ConfigValidator.Validate(config, load.Problems)).ToList();
        var rejected = ConfigValidator.FlowsWithErrors(problems);
        var flows = config.Flows.Select(flow => new FlowState(
            flow.Id,
            rejected.Contains(flow.Id) ? previous.Flows.FirstOrDefault(f => f.FlowId == flow.Id)?.Running : flow,
            new(problems.Where(p => p.FlowId == flow.Id))));
        return new ConfigSnapshot(config.Settings, config.Library, new(flows), new(problems));
    }

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
            _watcher.EnableRaisingEvents = true;
        }

        public event Action? Changed;

        public void Dispose() => _watcher?.Dispose();
    }
}
