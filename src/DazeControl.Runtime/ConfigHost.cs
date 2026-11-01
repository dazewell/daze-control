using DazeControl.Core.Catalog;
using DazeControl.Core.Input;
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
            Changed?.Invoke(next);
        }
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
    /// The D-021 rule: per flow, the new version if valid, else the previous running one, else none. A retained
    /// version keeps what it was validated with: the library entries it references, and only hotkeys no current
    /// flow has taken.
    /// </summary>
    internal static ConfigSnapshot Apply(ConfigSnapshot previous, ConfigLoad load)
    {
        if (load.Config is not { } config)
            return previous with { Problems = load.Problems };
        var problems = load.Problems.Concat(ConfigValidator.Validate(config, load.Problems)).ToList();
        var rejected = ConfigValidator.FlowsWithErrors(problems);
        var retained = config.Flows
            .Where(f => rejected.Contains(f.Id))
            .Select(f => previous.Flows.FirstOrDefault(p => p.FlowId == f.Id)?.Running)
            .OfType<Flow>()
            .ToDictionary(f => f.Id);

        // Hotkeys of current flows were checked against each other; a retained version yields to them.
        var taken = config.Flows.Where(f => !rejected.Contains(f.Id) && f.Enabled)
            .SelectMany(f => f.Hotkeys).Select(ParseHotkey).OfType<Hotkey>().ToHashSet();
        foreach (var (id, flow) in retained.Where(r => r.Value.Enabled).ToList())
        {
            var keep = flow.Hotkeys.Where(text => ParseHotkey(text) is not { } hotkey || taken.Add(hotkey)).ToList();
            if (keep.Count == flow.Hotkeys.Count)
                continue;
            retained[id] = flow with { Hotkeys = new(keep) };
            problems.Add(new Problem(Severity.Warning, $"flows.{id}.hotkeys",
                $"The last valid version of this flow lost {string.Join(", ", flow.Hotkeys.Except(keep))} to another flow.", id));
        }

        var flows = config.Flows.Select(flow => new FlowState(
            flow.Id,
            rejected.Contains(flow.Id) ? retained.GetValueOrDefault(flow.Id) : flow,
            new(problems.Where(p => p.FlowId == flow.Id))));
        return new ConfigSnapshot(config.Settings, LibraryFor(config.Library, previous.Library, retained.Values),
            new(flows), new(problems));
    }

    private static Hotkey? ParseHotkey(string text) => Hotkey.TryParse(text, out var hotkey) ? hotkey : null;

    /// <summary>The new library, with the previous entries that retained flows reference put back.</summary>
    private static Library LibraryFor(Library current, Library previous, IEnumerable<Flow> retained)
    {
        var refs = retained.SelectMany(LibraryRefs).ToHashSet();
        if (refs.Count == 0)
            return current;
        return current with
        {
            Audio = new(Overlay(current.Audio, previous.Audio, d => d.Key, k => refs.Contains((LibraryKind.Audio, k)))),
            Bluetooth = new(Overlay(current.Bluetooth, previous.Bluetooth, d => d.Key, k => refs.Contains((LibraryKind.Bluetooth, k)))),
        };
    }

    private static IEnumerable<T> Overlay<T>(IEnumerable<T> current, IEnumerable<T> previous, Func<T, string> key, Func<string, bool> pinned)
    {
        var old = previous.Where(e => pinned(key(e))).ToDictionary(key);
        foreach (var entry in current)
            yield return old.Remove(key(entry), out var kept) ? kept : entry;
        foreach (var kept in old.Values)
            yield return kept;
    }

    private static IEnumerable<(LibraryKind, string)> LibraryRefs(Flow flow) =>
        from step in flow.Nodes.OfType<StepNode>()
        let descriptor = ProviderCatalog.All.FirstOrDefault(d => d.Id == step.Step)
        where descriptor is not null
        from param in step.With
        let spec = descriptor.FindParam(param.Key)
        where spec is { Kind: ParamKind.LibraryRef, Library: not null } && param.Value is ScalarParam
        select (spec.Library!.Value, ((ScalarParam)param.Value).Text);

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
