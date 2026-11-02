using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.LibraryPage;

/// <summary>One Pivot item of the Library page (UX §5.1). <see cref="Kind"/> is null for Programs.</summary>
public sealed partial class LibrarySectionViewModel : ObservableObject
{
    internal LibrarySectionViewModel(LibraryKind? kind, string title, string glyph, string emptyText)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
        EmptyText = emptyText;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public LibraryKind? Kind { get; }

    public string Title { get; }

    public string Glyph { get; }

    public string EmptyText { get; }

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    /// <summary>This PC's devices that aren't in the Library yet ("From Windows"), for Add.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailable))]
    public partial IReadOnlyList<DeviceOption> Available { get; internal set; } = [];

    public bool HasAvailable => Available.Count > 0;

    /// <summary>Devices can be added from Windows; Displays capture the current layout; Programs come from flows.</summary>
    public bool CanAdd => Kind is LibraryKind.Audio or LibraryKind.Bluetooth or LibraryKind.FireTv;

    public bool CanCapture => Kind == LibraryKind.Display;

    [ObservableProperty]
    public partial bool IsCapturing { get; internal set; }

    /// <summary>Why the last Add or Capture didn't happen, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; internal set; }

    public bool HasNote => Note is not null;

    /// <summary>The Pivot header's name for screen readers.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The Library page (UX §5.1): Audio, Bluetooth, Displays, Fire TV and Programs as settings cards with live state,
/// "Used by 2 flows" and a read-only Test. Programs are the <c>process.run</c> paths the flows use; there is no
/// Library kind for them. UI thread only; engine answers come back through <c>post</c>.
/// </summary>
public sealed partial class LibraryPageViewModel : ObservableObject, IDisposable
{
    private const string ProgramStep = "process.run", ProgramParam = "path";

    private readonly ConfigSession _session;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _searchPath;
    private readonly Dictionary<LibraryKind, IReadOnlyList<DeviceInfo>> _live = [];

    /// <param name="fileExists">Seam for tests; the real file system by default.</param>
    /// <param name="searchPath">PATH for programs named without a folder; the process's by default.</param>
    public LibraryPageViewModel(ConfigSession session, IEngineClient engine, Action<Action> post,
        Func<string, bool>? fileExists = null, Func<string?>? searchPath = null)
    {
        _session = session;
        _engine = engine;
        _post = post;
        _fileExists = fileExists ?? File.Exists;
        _searchPath = searchPath ?? (() => Environment.GetEnvironmentVariable("PATH"));
        Sections =
        [
            new(LibraryKind.Audio, "Audio", LibraryDevices.Glyph(LibraryKind.Audio),
                "No audio devices yet. Add one from Windows, or pick one in a step."),
            new(LibraryKind.Bluetooth, "Bluetooth", LibraryDevices.Glyph(LibraryKind.Bluetooth),
                "No Bluetooth devices yet. Add a paired one from Windows."),
            new(LibraryKind.Display, "Displays", LibraryDevices.Glyph(LibraryKind.Display),
                "No screen layouts yet. Set your screens up the way you like, then capture the layout."),
            new(LibraryKind.FireTv, "Fire TV", LibraryDevices.Glyph(LibraryKind.FireTv),
                "No Fire TVs yet. Turn on network debugging on the TV, connect it with adb, then add it."),
            new(null, "Programs", FlowItemViewModel.DecodeGlyph("E756"),
                "No programs yet. Programs show up here once a flow runs one."),
        ];
        _session.PropertyChanged += OnSessionChanged;
        _engine.StateChanged += OnEngineStateChanged;
        Sync();
    }

    public IReadOnlyList<LibrarySectionViewModel> Sections { get; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    public LibrarySectionViewModel Section(LibraryKind? kind) => Sections.First(s => s.Kind == kind);

    public bool CanEdit => _session.CanEdit;

    /// <summary>Asks the engine what this PC has now, for every device kind (pipe <c>devices</c>).</summary>
    public Task RefreshAsync() => Task.WhenAll(Sections.Where(s => s.Kind is not null).Select(s => RefreshAsync(s.Kind!.Value)));

    public async Task RefreshAsync(LibraryKind kind)
    {
        var devices = await ListAsync(kind, CancellationToken.None).ConfigureAwait(false);
        _post(() =>
        {
            if (devices is not { Ok: true, Devices: { } list })
                _live.Remove(kind);
            else
                _live[kind] = list;
            Sync(kind);
        });
    }

    // ---- Counting (UX §5.1 "Used by 2 flows") ----

    /// <summary>How many flows have a step whose Library setting of this kind names the key.</summary>
    public static int UsedBy(Configuration config, LibraryKind kind, string key) =>
        config.Flows.Count(f => f.Nodes.OfType<StepNode>().Any(step => References(step, kind, key)));

    private static bool References(StepNode step, LibraryKind kind, string key) =>
        ProviderCatalog.Find(step.Step) is { } descriptor
        && descriptor.Params.Any(p => p.Kind == ParamKind.LibraryRef && p.Library == kind
            && step.With.TryGetValue(p.Name, out var value) && value is ScalarParam { Text: var text } && text == key);

    /// <summary>The programs the flows run, each with how many flows run it (paths compared ignoring case).</summary>
    public static IReadOnlyList<(string Path, int Flows)> Programs(Configuration config) =>
    [
        .. config.Flows
            .SelectMany(f => f.Nodes.OfType<StepNode>().Select(ProgramPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First(), g.Count()))
            .OrderBy(p => Path.GetFileName(p.Item1), StringComparer.CurrentCultureIgnoreCase),
    ];

    private static string? ProgramPath(StepNode step) =>
        step.Step == ProgramStep && step.With.TryGetValue(ProgramParam, out var value) && value is ScalarParam { Text: var text }
            && text.Trim().Trim('"') is { Length: > 0 } path
            ? path
            : null;

    // ---- Sync from config.yaml and the live lists ----

    private void Sync()
    {
        foreach (var section in Sections)
            Sync(section.Kind);
    }

    private void Sync(LibraryKind? kind)
    {
        var section = Section(kind);
        var config = _session.Current;
        List<(string Key, string Title, string Detail, string State, int UsedBy, string? Problem)> rows;
        if (kind is { } k)
        {
            var live = _live.GetValueOrDefault(k);
            rows = [.. LibraryDevices.Items(config.Library, k).Select(i => (i.Key, i.Title, Detail(config.Library, k, i.Key),
                k == LibraryKind.Display ? "" : LibraryDevices.State(config.Library, k, i.Key, live),
                UsedBy(config, k, i.Key), ProblemOf(k, i.Key)))];
            section.Available = live is null || !section.CanAdd
                ? []
                : [.. LibraryDevices.Options(k, config.Library, live, null, required: true, canCapture: false)
                    .Where(o => o.Source == DeviceSource.Windows)];
        }
        else
        {
            rows = [.. Programs(config).Select(p => (p.Path, ProgramTitle(p.Path), ProgramDetail(p.Path), "", p.Flows, (string?)null))];
        }

        var existing = section.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        section.Items.Clear();
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Key, out var item))
                item = new LibraryItemViewModel(row.Key, section.Glyph, (i, ct) => TestAsync(kind, i.Key, ct));
            item.Title = row.Title;
            item.Detail = row.Detail;
            item.State = row.State;
            item.UsedBy = row.UsedBy;
            item.Problem = row.Problem;
            item.Removable = kind is not null && CanEdit;
            section.Items.Add(item);
        }
    }

    private static string Detail(Library library, LibraryKind kind, string key) => kind switch
    {
        LibraryKind.Audio => library.Audio.First(d => d.Key == key) is var a && a.Flow == AudioFlow.Input ? "Input" : "Output",
        LibraryKind.Bluetooth => library.Bluetooth.First(d => d.Key == key).Address,
        LibraryKind.Display => LibraryDevices.State(library, kind, key, null),
        _ => library.FireTv.First(d => d.Key == key).Address,
    };

    private static string ProgramTitle(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    private static string ProgramDetail(string path) =>
        Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : "Found through PATH";

    private string? ProblemOf(LibraryKind kind, string key)
    {
        var prefix = $"library.{YamlSection(kind)}.{key}";
        return _session.Problems.FirstOrDefault(p => p.Path == prefix || p.Path.StartsWith(prefix + ".", StringComparison.Ordinal))?.Message;
    }

    private static string YamlSection(LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => "audio",
        LibraryKind.Bluetooth => "bluetooth",
        LibraryKind.Display => "displays",
        _ => "firetv",
    };

    // ---- Test (read-only: never connects, switches or launches anything) ----

    private Task<PipeMessage?> ListAsync(LibraryKind kind, CancellationToken cancellationToken) =>
        _engine.SendAsync(new PipeRequest { Op = Ops.Devices, Kind = LibraryDevices.PipeKind(kind) }, cancellationToken);

    private async Task<TestResult> TestAsync(LibraryKind? kind, string key, CancellationToken cancellationToken)
    {
        if (kind is not { } k)
            return TestProgram(key);
        var library = _session.Current.Library;
        if (k == LibraryKind.Display && library.Displays.FirstOrDefault(d => d.Key == key) is { } bad && !bad.IsWellFormed())
            return new(TestOutcome.Failed, "This layout is damaged. Capture it again.");
        var answer = await ListAsync(k, cancellationToken).ConfigureAwait(false);
        var result = Judge(_session.Current.Library, k, key, answer);
        _post(() =>
        {
            if (answer is { Ok: true, Devices: { } list })
                _live[k] = list;
            Sync(k);
        });
        return result;
    }

    /// <summary>What a Test says about a Library item, given the engine's live list (null: no engine).</summary>
    public static TestResult Judge(Library library, LibraryKind kind, string key, PipeMessage? answer)
    {
        if (answer is null)
            return new(TestOutcome.Failed, "Start Daze Control to test this.");
        if (!answer.Ok || answer.Devices is not { } live)
            return new(TestOutcome.Failed, $"Couldn't check: {answer.Error ?? "Daze Control didn't say why."}");
        var title = LibraryDevices.Items(library, kind).FirstOrDefault(i => i.Key == key).Title ?? key;
        if (kind == LibraryKind.Display)
        {
            if (library.Displays.FirstOrDefault(d => d.Key == key) is not { } layout)
                return new(TestOutcome.Failed, $"\"{key}\" isn't in your Library anymore.");
            var here = live.Select(d => d.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = layout.Monitors.Count(m => !here.Contains(m));
            return missing switch
            {
                0 when layout.Monitors.Count == 1 => new(TestOutcome.Worked, "Its screen is connected."),
                0 => new(TestOutcome.Worked, $"All {layout.Monitors.Count} of its screens are connected."),
                _ when missing == layout.Monitors.Count => new(TestOutcome.Failed, "None of its screens are connected right now."),
                1 => new(TestOutcome.Failed, $"1 of its {layout.Monitors.Count} screens isn't connected right now."),
                _ => new(TestOutcome.Failed, $"{missing} of its {layout.Monitors.Count} screens aren't connected right now."),
            };
        }
        var device = LibraryDevices.Match(library, kind, key, live);
        return (kind, device) switch
        {
            (LibraryKind.FireTv, null) => new(TestOutcome.Caution,
                $"adb isn't connected to {title} right now. The flow connects first, so make sure the TV is on."),
            (_, null) => new(TestOutcome.Failed, kind == LibraryKind.Bluetooth
                ? $"Windows couldn't find {title}. Make sure it's paired, then try again."
                : $"Windows couldn't find {title}. Make sure it's plugged in or turned on, then try again."),
            (LibraryKind.Bluetooth, _) when !string.Equals(LibraryDevices.Describe(kind, device), "Connected", StringComparison.Ordinal) =>
                new(TestOutcome.Caution, $"Found {title}. Not connected right now. The flow will still try."),
            _ => new(TestOutcome.Worked, LibraryDevices.Describe(kind, device) is { Length: > 0 } state
                ? $"Found {title}: {char.ToLowerInvariant(state[0])}{state[1..]}."
                : $"Found {title}."),
        };
    }

    /// <summary>A full path must exist; a bare name ("notepad.exe") is looked up on PATH as Windows would.</summary>
    public TestResult TestProgram(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
            return _fileExists(expanded)
                ? new(TestOutcome.Worked, "Found the program.")
                : new(TestOutcome.Failed, "Can't find this file. Check the path in the flows that run it.");
        var extensions = Path.HasExtension(expanded) ? [""] : new[] { ".exe", ".cmd", ".bat", ".com" };
        foreach (var folder in (_searchPath() ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, expanded + extension);
                if (_fileExists(candidate))
                    return new(TestOutcome.Worked, $"Found it: {candidate}");
            }
        }
        return new(TestOutcome.Failed, "Can't find this program on PATH. Use its full path in the flows that run it.");
    }

    // ---- Add, capture, remove ----

    /// <summary>A device from Windows joins the Library under a key made from its name.</summary>
    [RelayCommand]
    private void Add(DeviceOption option)
    {
        var section = Sections.FirstOrDefault(s => s.Available.Contains(option));
        if (!CanEdit || section?.Kind is not { } kind || option.Device is not { } device)
            return;
        section.Note = null;
        _session.Update(c => c with { Library = LibraryDevices.Add(c.Library, kind, device).Library });
    }

    /// <summary>Displays: saves how the screens are set up now as a new layout (reads only, changes nothing).</summary>
    [RelayCommand]
    private async Task CaptureAsync()
    {
        var section = Section(LibraryKind.Display);
        if (!CanEdit || section.IsCapturing)
            return;
        section.IsCapturing = true;
        section.Note = null;
        var key = LibraryDevices.NewKey(_session.Current.Library, LibraryKind.Display, "layout");
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.CaptureDisplayLayout, Key = key }).ConfigureAwait(false);
        _post(() =>
        {
            section.IsCapturing = false;
            var captured = answer is { Ok: true, Yaml: { } yaml } && ConfigYaml.Load(yaml).Config?.Library.Displays is { Count: > 0 } displays
                ? displays[0]
                : null;
            if (captured is null)
            {
                section.Note = answer is null ? "Start Daze Control to capture the layout." : $"Couldn't capture the layout: {answer.Error ?? "no screens found."}";
                return;
            }
            var unique = LibraryDevices.NewKey(_session.Current.Library, LibraryKind.Display, captured.Key);
            _session.Update(c => c with { Library = c.Library with { Displays = [.. c.Library.Displays, captured with { Key = unique }] } });
        });
    }

    /// <summary>Only items no flow uses: removing one in use would break those flows.</summary>
    [RelayCommand]
    private void Remove(LibraryItemViewModel item)
    {
        var kind = Sections.FirstOrDefault(s => s.Items.Contains(item))?.Kind;
        if (!CanEdit || kind is not { } k || UsedBy(_session.Current, k, item.Key) > 0)
            return;
        _session.Update(c => c with
        {
            Library = k switch
            {
                LibraryKind.Audio => c.Library with { Audio = [.. c.Library.Audio.Where(d => d.Key != item.Key)] },
                LibraryKind.Bluetooth => c.Library with { Bluetooth = [.. c.Library.Bluetooth.Where(d => d.Key != item.Key)] },
                LibraryKind.Display => c.Library with { Displays = [.. c.Library.Displays.Where(d => d.Key != item.Key)] },
                _ => c.Library with { FireTv = [.. c.Library.FireTv.Where(d => d.Key != item.Key)] },
            },
        });
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfigSession.Current) or nameof(ConfigSession.Problems) or nameof(ConfigSession.CanEdit))
            Sync();
        if (e.PropertyName == nameof(ConfigSession.CanEdit))
            OnPropertyChanged(nameof(CanEdit));
    }

    private void OnEngineStateChanged() => _ = RefreshAsync();

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _engine.StateChanged -= OnEngineStateChanged;
    }
}
