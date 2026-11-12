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
    public bool CanAdd => Kind is LibraryKind.Audio or LibraryKind.Bluetooth or LibraryKind.Android;

    public bool CanCapture => Kind == LibraryKind.Display;

    [ObservableProperty]
    public partial bool IsCapturing { get; internal set; }

    /// <summary>Why the last Add or Capture didn't happen, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; internal set; }

    public bool HasNote => Note is not null;

    // ---- Bound devices (D-116): the "Bind devices" form ----

    public bool CanBind => Kind == LibraryKind.Bound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBindChoices))]
    public partial IReadOnlyList<LibraryChoice> BindBluetoothChoices { get; internal set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBindChoices))]
    public partial IReadOnlyList<LibraryChoice> BindAudioChoices { get; internal set; } = [];

    /// <summary>Binding needs a Bluetooth device and an audio device in the Library.</summary>
    public bool HasBindChoices => BindBluetoothChoices.Count > 0 && BindAudioChoices.Count > 0;

    [ObservableProperty]
    public partial LibraryChoice? BindBluetooth { get; set; }

    [ObservableProperty]
    public partial LibraryChoice? BindAudio { get; set; }

    [ObservableProperty]
    public partial string BindName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBindError))]
    public partial string? BindError { get; internal set; }

    public bool HasBindError => BindError is not null;

    /// <summary>A new name follows the Bluetooth device picked, until the user types their own.</summary>
    partial void OnBindAudioChanged(LibraryChoice? value) => BindError = null;

    partial void OnBindNameChanged(string value) => BindError = null;

    partial void OnBindBluetoothChanged(LibraryChoice? oldValue, LibraryChoice? newValue)
    {
        BindError = null;
        if (newValue is not null && (BindName.Length == 0 || BindName == oldValue?.Title))
            BindName = newValue.Title;
    }

    // ---- Add by hand (D-123): a device typed in, for one that's off, away or not paired yet ----

    public bool CanAddByHand => Kind is { } kind && ManualItems.CanAdd(kind);

    public bool IsAudio => Kind == LibraryKind.Audio;

    /// <summary>Bluetooth and Android items need a name (their key); an audio device's name text is its name.</summary>
    public bool NeedsName => Kind is LibraryKind.Bluetooth or LibraryKind.Android;

    /// <summary>The field's header: what identifies the device for this kind.</summary>
    public string ManualValueHeader => Kind switch
    {
        LibraryKind.Audio => "Name in Windows (or a part of it)",
        LibraryKind.Bluetooth => "Bluetooth address",
        _ => "Network address",
    };

    public string ManualValuePlaceholder => Kind switch
    {
        LibraryKind.Audio => "Technics",
        LibraryKind.Bluetooth => "B8:20:8E:ED:48:FC",
        _ => "192.168.1.50 or 192.168.1.50:5555",
    };

    public string ManualNamePlaceholder => Kind == LibraryKind.Android ? "Living room TV" : "Technics headphones";

    public string ManualHint => Kind switch
    {
        LibraryKind.Audio => "Steps use the first sound device whose name contains this text.",
        LibraryKind.Bluetooth => "Find it in Settings › Bluetooth & devices, or on the device's box. Connect works once it's paired.",
        _ => "Turn on network debugging on the device (Developer options).",
    };

    [ObservableProperty]
    public partial string ManualValue { get; set; } = "";

    [ObservableProperty]
    public partial string ManualName { get; set; } = "";

    /// <summary>Audio only: 0 = Output (playback), 1 = Input (recording).</summary>
    [ObservableProperty]
    public partial int ManualFlowIndex { get; set; }

    public IReadOnlyList<string> ManualFlows { get; } = ["Output (speakers, headphones)", "Input (microphone)"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualError), nameof(ManualErrorOpacity))]
    public partial string? ManualError { get; internal set; }

    public bool HasManualError => ManualError is not null;

    /// <summary>The error line keeps its space (no jump when it shows).</summary>
    public double ManualErrorOpacity => HasManualError ? 1 : 0;

    /// <summary>The Pivot header's name for screen readers.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The Library page (UX §5.1): Audio, Bluetooth, Displays, Android and Programs as settings cards with live state,
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
    private readonly Action<string> _reveal;
    private readonly Dictionary<LibraryKind, IReadOnlyList<DeviceInfo>> _live = [];

    /// <param name="fileExists">Seam for tests; the real file system by default.</param>
    /// <param name="searchPath">PATH for programs named without a folder; the process's by default.</param>
    /// <param name="reveal">Shows a file selected in File Explorer; the real one by default.</param>
    public LibraryPageViewModel(ConfigSession session, IEngineClient engine, Action<Action> post,
        Func<string, bool>? fileExists = null, Func<string?>? searchPath = null, Action<string>? reveal = null)
    {
        _session = session;
        _engine = engine;
        _post = post;
        _fileExists = fileExists ?? File.Exists;
        _searchPath = searchPath ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _reveal = reveal ?? RevealInExplorer;
        Sections =
        [
            new(LibraryKind.Audio, "Audio", LibraryDevices.Glyph(LibraryKind.Audio),
                "No audio devices yet. Add one from Windows or by hand, or pick one in a step."),
            new(LibraryKind.Bluetooth, "Bluetooth", LibraryDevices.Glyph(LibraryKind.Bluetooth),
                "No Bluetooth devices yet. Add a paired one from Windows, or add one by hand by its address."),
            new(LibraryKind.Bound, "Bound devices", LibraryDevices.Glyph(LibraryKind.Bound),
                "No bound devices yet. Bind a Bluetooth device to its audio device to use the pair as one device in Bluetooth and audio steps."),
            new(LibraryKind.Display, "Displays", LibraryDevices.Glyph(LibraryKind.Display),
                "No screen layouts yet. Set your screens up the way you like, then capture the layout."),
            new(LibraryKind.Android, "Android", LibraryDevices.Glyph(LibraryKind.Android),
                "No Android devices yet. Turn on network debugging on the TV, then add it from Windows or by hand by its address."),
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

    /// <summary>A "Used by" flow was picked: the shell opens it in the flow editor.</summary>
    public event Action<string>? FlowRequested;

    /// <summary>Asks the engine what this PC has now, for every device kind (pipe <c>devices</c>).</summary>
    public Task RefreshAsync() =>
        Task.WhenAll(Sections.Where(s => s.Kind is { } k && k != LibraryKind.Bound).Select(s => RefreshAsync(s.Kind!.Value)));

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
            if (kind == LibraryKind.Bluetooth)
                Sync(LibraryKind.Bound);
        });
    }

    // ---- Counting (UX §5.1 "Used by 2 flows") ----

    /// <summary>How many flows have a step whose Library setting of this kind names the key.</summary>
    public static int UsedBy(Configuration config, LibraryKind kind, string key) => Users(config, kind, key).Count;

    /// <summary>The flows that use the key: a step setting, an input's default or a value passed to a call (D-064), in file order.</summary>
    public static IReadOnlyList<FlowLink> Users(Configuration config, LibraryKind kind, string key) =>
        [.. config.Flows.Where(f => LibraryEdits.Uses(f, config, kind, key)).Select(Link)];

    /// <summary>The flows that run this program (paths compared ignoring case), in file order.</summary>
    public static IReadOnlyList<FlowLink> ProgramUsers(Configuration config, string path) =>
        [.. config.Flows.Where(f => f.Nodes.OfType<StepNode>().Any(step => string.Equals(ProgramPath(step), path, StringComparison.OrdinalIgnoreCase))).Select(Link)];

    private static FlowLink Link(Flow flow) => new(flow.Id, flow.Name.Length > 0 ? flow.Name : flow.Id);

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
        List<(string Key, string Title, string Detail, string State, IReadOnlyList<FlowLink> Users, string? Problem)> rows;
        // A bound device's state is its Bluetooth side's (D-116).
        var live = kind is { } liveKind ? _live.GetValueOrDefault(liveKind == LibraryKind.Bound ? LibraryKind.Bluetooth : liveKind) : null;
        if (kind is { } k)
        {
            rows = [.. LibraryDevices.Items(config.Library, k).Select(i => (i.Key, i.Title, Detail(config.Library, k, i.Key),
                k == LibraryKind.Display ? ""
                : k == LibraryKind.Bound ? LibraryDevices.State(config.Library, LibraryKind.Bluetooth, i.Key, live)
                : LibraryDevices.State(config.Library, k, i.Key, live),
                Users(config, k, i.Key), ProblemOf(k, i.Key)))];
            section.Available = live is null || !section.CanAdd
                ? []
                : [.. LibraryDevices.Options(k, config.Library, live, null, required: true, canCapture: false)
                    .Where(o => o.Source == DeviceSource.Windows)];
        }
        else
        {
            rows = [.. Programs(config).Select(p => (p.Path, ProgramTitle(p.Path), ProgramDetail(p.Path), "", ProgramUsers(config, p.Path), (string?)null))];
        }

        var existing = section.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        section.Items.Clear();
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Key, out var item))
                item = new LibraryItemViewModel(row.Key, section.Glyph, (i, ct) => TestAsync(kind, i.Key, ct))
                {
                    CanOpenLocation = kind is null,
                    HasInputs = kind == LibraryKind.Android,
                    SidesChanged = kind == LibraryKind.Bound ? SetSides : null,
                };
            item.Title = row.Title;
            item.Detail = row.Detail;
            item.State = row.State;
            item.UsedBy = row.Users.Count;
            item.UsedByFlows = row.Users;
            item.Problem = row.Problem;
            item.Removable = kind is not null && CanEdit;
            item.CanRename = kind is LibraryKind.Display or LibraryKind.Android or LibraryKind.Bluetooth or LibraryKind.Bound && CanEdit;
            item.RenameText = kind is LibraryKind.Bluetooth or LibraryKind.Bound ? row.Title : row.Key;
            item.BoundIn = kind is { } side && config.Library.BoundWith(side, row.Key).FirstOrDefault() is { } binding
                ? binding.Name ?? binding.Key
                : null;
            if (kind == LibraryKind.Bound)
            {
                var bound = config.Library.Bound.First(d => d.Key == row.Key);
                var (bluetooth, audio) = (Choices(config.Library, LibraryKind.Bluetooth), Choices(config.Library, LibraryKind.Audio));
                item.LoadSides(bluetooth, audio, bluetooth.FirstOrDefault(c => c.Key == bound.Bluetooth),
                    audio.FirstOrDefault(c => c.Key == bound.Audio), CanEdit);
            }
            if (kind == LibraryKind.Display)
                item.Screens = ScreenDetails(config.Library.Displays.First(d => d.Key == row.Key), live);
            if (kind == LibraryKind.Android)
                SyncInputs(item, config, config.Library.Android.First(d => d.Key == row.Key));
            section.Items.Add(item);
        }
        if (kind == LibraryKind.Bound)
        {
            section.BindBluetoothChoices = Choices(config.Library, LibraryKind.Bluetooth);
            section.BindAudioChoices = Choices(config.Library, LibraryKind.Audio);
        }
    }

    private static List<LibraryChoice> Choices(Library library, LibraryKind kind) =>
        [.. LibraryDevices.Items(library, kind).Select(i => new LibraryChoice(i.Key, kind == LibraryKind.Audio ? AudioTitle(library, i.Key) : i.Title))];

    /// <summary>"Technics (Input)": an audio device's match, with its direction when it's an input.</summary>
    private static string AudioTitle(Library library, string key) =>
        library.Audio.First(d => d.Key == key) is var a && a.Flow == AudioFlow.Input ? $"{a.Match} (Input)" : a.Match;

    // ---- Bound devices (D-116) ----

    /// <summary>Library › Bound devices › Bind: a new bound device of the section's picks. False, with
    /// <see cref="LibrarySectionViewModel.BindError"/> saying why, when a side or the name is missing.</summary>
    public bool Bind()
    {
        var section = Section(LibraryKind.Bound);
        var name = section.BindName.Trim();
        section.BindError = !CanEdit ? "The Library can't be changed right now."
            : section.BindBluetooth is null ? "Pick a Bluetooth device."
            : section.BindAudio is null ? "Pick an audio device."
            : name.Length == 0 ? "Give it a name."
            : null;
        if (section.BindError is not null)
            return false;
        var (bluetooth, audio) = (section.BindBluetooth!.Key, section.BindAudio!.Key);
        var key = LibraryDevices.NewKey(_session.Current.Library, LibraryKind.Bound, name);
        _session.Update(c => c with
        {
            Library = c.Library with { Bound = [.. c.Library.Bound, new BoundDevice(key, bluetooth, audio, name == key ? null : name)] },
        });
        section.BindBluetooth = null;
        section.BindAudio = null;
        section.BindName = "";
        return true;
    }

    /// <summary>A bound device's card changed a side: steps using it follow at once (autosaved like the rest of the Library).</summary>
    private void SetSides(LibraryItemViewModel item)
    {
        if (!CanEdit || item.BoundBluetooth is not { } bluetooth || item.BoundAudio is not { } audio)
            return;
        _session.Update(c => c with
        {
            Library = c.Library with
            {
                Bound = [.. c.Library.Bound.Select(d => d.Key == item.Key ? d with { Bluetooth = bluetooth.Key, Audio = audio.Key } : d)],
            },
        });
    }

    /// <summary>Android: the inputs in file order; rows of unchanged names stay (an open rename keeps its box).</summary>
    private void SyncInputs(LibraryItemViewModel item, Configuration config, AndroidDevice device)
    {
        item.CanEditInputs = CanEdit;
        item.NewInputName = AndroidInputEdits.NewName(device);
        var names = device.Inputs.Keys.ToList();
        if (!item.Inputs.Select(i => i.Name).SequenceEqual(names))
        {
            var existing = item.Inputs.ToDictionary(i => i.Name, StringComparer.Ordinal);
            item.Inputs.Clear();
            foreach (var name in names)
                item.Inputs.Add(existing.GetValueOrDefault(name) ?? new AndroidInputViewModel(device.Key, name));
        }
        foreach (var input in item.Inputs)
            input.Load(device.Inputs[input.Name], AndroidInputEdits.Users(config, device.Key, input.Name).Count, CanEdit);
    }

    private static string Detail(Library library, LibraryKind kind, string key) => kind switch
    {
        LibraryKind.Audio => library.Audio.First(d => d.Key == key) is var a && a.Flow == AudioFlow.Input ? "Input" : "Output",
        LibraryKind.Bluetooth => library.Bluetooth.First(d => d.Key == key).Address,
        LibraryKind.Display => LibraryDevices.State(library, kind, key, null),
        LibraryKind.Bound => BoundDetail(library, library.Bound.First(d => d.Key == key)),
        _ => AndroidDetail(library.Android.First(d => d.Key == key)),
    };

    /// <summary>"Technics EAH-AZ80 + Technics": its Bluetooth device and its audio device.</summary>
    private static string BoundDetail(Library library, BoundDevice bound)
    {
        var bluetooth = LibraryDevices.NameOf(library, LibraryKind.Bluetooth, bound.Bluetooth);
        var audio = library.Audio.FirstOrDefault(d => d.Key == bound.Audio)?.Match ?? bound.Audio;
        // A name that is just the Bluetooth device's would repeat itself in the subtitle (F-007).
        return (bound.Name ?? bound.Key) == bluetooth ? $"Audio: {audio}" : $"{bluetooth} + {audio}";
    }

    /// <summary>"192.168.1.50 · 2 inputs".</summary>
    private static string AndroidDetail(AndroidDevice device) => device.Inputs.Count switch
    {
        0 => $"{device.Address} · No inputs yet",
        1 => $"{device.Address} · 1 input",
        var n => $"{device.Address} · {n} inputs",
    };

    /// <summary>
    /// A layout's screens from its capture. A plugged-in screen (matched by device path in the live list) shows its
    /// name and port as Windows has them now; another shows its maker/model code and cable type.
    /// </summary>
    public static IReadOnlyList<ScreenDetail> ScreenDetails(DisplayLayout layout, IReadOnlyList<DeviceInfo>? live) =>
    [
        .. LayoutScreens.Read(layout).Select(screen =>
        {
            var now = live?.FirstOrDefault(d => string.Equals(d.Id, screen.Monitor, StringComparison.OrdinalIgnoreCase));
            // The live detail is "DELL S2722QC, on" (or just "on" when Windows has no name for it).
            var name = now?.Detail is { } detail && detail.LastIndexOf(", ", StringComparison.Ordinal) is > 0 and var comma
                ? detail[..comma]
                : screen.HardwareId;
            return new ScreenDetail(now is null ? $"{name} (not connected)" : name, now?.Name ?? screen.Connection,
                screen.Resolution, screen.Position, screen.Refresh, screen.Primary);
        }),
    ];

    private static string ProgramTitle(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    private static string ProgramDetail(string path) =>
        Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : "Found through PATH";

    private string? ProblemOf(LibraryKind kind, string key)
    {
        var section = $"library.{YamlSection(kind)}.";
        // Keys may contain dots ("sony.spare"): a path belongs to the longest key it starts with.
        var keys = LibraryDevices.Items(_session.Current.Library, kind).Select(i => i.Key).OrderByDescending(k => k.Length).ToList();
        return _session.Problems.FirstOrDefault(p => p.Path.StartsWith(section, StringComparison.Ordinal)
            && keys.FirstOrDefault(k => p.Path[section.Length..] == k || p.Path[section.Length..].StartsWith(k + ".", StringComparison.Ordinal)) == key)?.Message;
    }

    private static string YamlSection(LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => "audio",
        LibraryKind.Bluetooth => "bluetooth",
        LibraryKind.Display => "displays",
        LibraryKind.Bound => "bound",
        _ => "android",
    };

    // ---- Test (read-only: never connects, switches or launches anything) ----

    private Task<PipeMessage?> ListAsync(LibraryKind kind, CancellationToken cancellationToken) =>
        _engine.SendAsync(new PipeRequest { Op = Ops.Devices, Kind = LibraryDevices.PipeKind(kind) }, cancellationToken);

    private async Task<TestResult> TestAsync(LibraryKind? kind, string key, CancellationToken cancellationToken)
    {
        if (kind is not { } k)
            return await Task.Run(() => TestProgram(key), cancellationToken).ConfigureAwait(false); // a network path can stall for seconds
        var library = _session.Current.Library;
        if (k == LibraryKind.Bound)
            return await TestBoundAsync(key, cancellationToken).ConfigureAwait(false);
        if (k == LibraryKind.Display && library.Displays.FirstOrDefault(d => d.Key == key) is { } bad && !bad.IsWellFormed())
            return new(TestOutcome.Failed, "This layout is damaged. Capture it again.");
        var answer = await ListAsync(k, cancellationToken).ConfigureAwait(false);
        var result = Judge(_session.Current.Library, k, key, answer);
        _post(() =>
        {
            if (answer is { Ok: true, Devices: { } list })
                _live[k] = list;
            Sync(k);
            if (k == LibraryKind.Bluetooth)
                Sync(LibraryKind.Bound);
        });
        return result;
    }

    /// <summary>A bound device's Test: each side's (D-116). Read-only like every Test.</summary>
    private async Task<TestResult> TestBoundAsync(string key, CancellationToken cancellationToken)
    {
        var bluetooth = ListAsync(LibraryKind.Bluetooth, cancellationToken);
        var audio = ListAsync(LibraryKind.Audio, cancellationToken);
        await Task.WhenAll(bluetooth, audio).ConfigureAwait(false);
        var (bt, au) = (await bluetooth.ConfigureAwait(false), await audio.ConfigureAwait(false));
        var result = JudgeBound(_session.Current.Library, key, bt, au);
        _post(() =>
        {
            if (bt is { Ok: true, Devices: { } btList })
                _live[LibraryKind.Bluetooth] = btList;
            if (au is { Ok: true, Devices: { } auList })
                _live[LibraryKind.Audio] = auList;
            Sync();
        });
        return result;
    }

    /// <summary>What a bound device's Test says: its Bluetooth side's result, then its audio side's; the worse outcome wins.</summary>
    public static TestResult JudgeBound(Library library, string key, PipeMessage? bluetooth, PipeMessage? audio)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (library.Bound.FirstOrDefault(d => d.Key == key) is not { } bound)
            return new(TestOutcome.Failed, $"\"{key}\" isn't in your Library anymore.");
        if (bluetooth is null && audio is null)
            return new(TestOutcome.Failed, "Start Daze Control to test this.");
        TestResult[] sides =
        [
            library.Bluetooth.Any(d => d.Key == bound.Bluetooth)
                ? Judge(library, LibraryKind.Bluetooth, bound.Bluetooth, bluetooth)
                : new(TestOutcome.Failed, Library.Missing(LibraryKind.Bluetooth, bound.Bluetooth)),
            library.Audio.Any(d => d.Key == bound.Audio)
                ? Judge(library, LibraryKind.Audio, bound.Audio, audio)
                : new(TestOutcome.Failed, Library.Missing(LibraryKind.Audio, bound.Audio)),
        ];
        return new(sides.Max(s => s.Outcome), string.Join(" ", sides.Select(s => s.Message).Distinct(StringComparer.Ordinal)));
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
            (LibraryKind.Android, null) => new(TestOutcome.Caution,
                $"adb isn't connected to {title} right now. The flow connects first, so make sure the TV is on."),
            (_, null) => new(TestOutcome.Failed, kind == LibraryKind.Bluetooth
                ? $"Windows couldn't find {title}. Make sure it's paired, then try again."
                : $"Windows couldn't find {title}. Make sure it's plugged in or turned on, then try again."),
            (LibraryKind.Android, _) when !string.Equals(LibraryDevices.Describe(kind, device), "Connected", StringComparison.Ordinal) =>
                new(TestOutcome.Caution, $"adb sees {title}, but it's {LibraryDevices.Describe(kind, device).ToLowerInvariant()}. "
                    + "Allow debugging on the TV if it asks, then try again."),
            (LibraryKind.Bluetooth, _) when !string.Equals(LibraryDevices.Describe(kind, device), "Connected", StringComparison.Ordinal) =>
                new(TestOutcome.Caution, $"Found {title}. Not connected right now. The flow will still try."),
            _ => new(TestOutcome.Worked, LibraryDevices.Describe(kind, device) is { Length: > 0 } state
                ? $"Found {title}: {char.ToLowerInvariant(state[0])}{state[1..]}."
                : $"Found {title}."),
        };
    }

    /// <summary>A full path must exist; a bare name ("notepad.exe") is looked up on PATH as Windows would.</summary>
    public TestResult TestProgram(string path) => ProgramResult(ResolveProgram(path));

    private static TestResult ProgramResult((string? File, bool OnPath) found) => found switch
    {
        (null, false) => new(TestOutcome.Failed, "Can't find this file. Check the path in the flows that run it."),
        (null, true) => new(TestOutcome.Failed, "Can't find this program on PATH. Use its full path in the flows that run it."),
        (_, false) => new(TestOutcome.Worked, "Found the program."),
        var (file, _) => new(TestOutcome.Worked, $"Found it: {file}"),
    };

    /// <summary>The program's file (null: not found), and whether it was looked up on PATH.</summary>
    private (string? File, bool OnPath) ResolveProgram(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
            return (_fileExists(expanded) ? expanded : null, false);
        var extensions = Path.HasExtension(expanded) ? [""] : new[] { ".exe", ".cmd", ".bat", ".com" };
        foreach (var folder in (_searchPath() ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, expanded + extension);
                if (_fileExists(candidate))
                    return (candidate, true);
            }
        }
        return (null, true);
    }

    /// <summary>Programs: shows the file selected in File Explorer, or says why it can't (the Test sentence).</summary>
    [RelayCommand]
    private async Task OpenLocationAsync(LibraryItemViewModel item)
    {
        var found = await Task.Run(() => ResolveProgram(item.Key)).ConfigureAwait(true); // a network path can stall
        if (found.File is null)
            item.ShowResult(ProgramResult(found)); // not TestProgram: that would look again, on the UI thread
        else
            _reveal(found.File);
    }

    private static void RevealInExplorer(string file) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();

    /// <summary>"Used by" › a flow: opens it in the flow editor.</summary>
    [RelayCommand]
    private void OpenFlow(FlowLink flow) => FlowRequested?.Invoke(flow.Id);

    /// <summary>
    /// Displays and Android: a new key, with every step that named the old one following it (D-046). False, with
    /// <see cref="LibraryItemViewModel.RenameError"/> saying why, when the name is empty or taken.
    /// </summary>
    public bool Rename(LibraryItemViewModel item, string newKey)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(newKey);
        if (!CanEdit || Sections.FirstOrDefault(s => s.Items.Contains(item))?.Kind is not ({ } kind and (LibraryKind.Display or LibraryKind.Android or LibraryKind.Bluetooth or LibraryKind.Bound)))
            return false;
        // Bluetooth and bound devices show a name that isn't their key: renaming changes the name, steps keep the key.
        var named = kind is LibraryKind.Bluetooth or LibraryKind.Bound;
        (Configuration Config, RenameRefusal Refusal) Edit(Configuration c) =>
            named ? LibraryEdits.Retitle(c, kind, item.Key, newKey) : LibraryEdits.Rename(c, kind, item.Key, newKey);
        var refusal = Edit(_session.Current).Refusal;
        item.RenameError = refusal switch
        {
            RenameRefusal.Empty => "Give it a name.",
            RenameRefusal.Taken => $"\"{newKey.Trim()}\" is already in your Library.",
            RenameRefusal.Missing => $"\"{item.Key}\" isn't in your Library anymore.",
            _ => null,
        };
        if (refusal != RenameRefusal.None)
            return false;
        _session.Update(c => Edit(c).Config);
        return true;
    }

    // ---- Android inputs (L1, D-078) ----

    /// <summary>
    /// Library › Android › Inputs: a new input with no moves, last in the list. False, with
    /// <see cref="LibraryItemViewModel.AddInputError"/> saying why, when the name is empty or taken.
    /// </summary>
    public bool AddInput(LibraryItemViewModel item, string name)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(name);
        if (!CanEdit || !item.HasInputs)
            return false;
        var refusal = AndroidInputEdits.Add(_session.Current, item.Key, name).Refusal;
        item.AddInputError = InputError(refusal, name, item.Key);
        if (refusal != InputEditRefusal.None)
            return false;
        // Applied to each version Update hands over, so an open flow's draft stays a draft (D-047).
        _session.Update(c => AndroidInputEdits.Add(c, item.Key, name).Config);
        return true;
    }

    /// <summary>A new name for an input; the <c>android.input</c> steps that picked it follow (D-046).</summary>
    public bool RenameInput(AndroidInputViewModel input, string newName)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(newName);
        if (!CanEdit)
            return false;
        var (config, refusal) = AndroidInputEdits.Rename(_session.Current, input.DeviceKey, input.Name, newName);
        input.RenameError = InputError(refusal, newName, input.DeviceKey);
        if (refusal != InputEditRefusal.None)
            return false;
        if (!ReferenceEquals(config, _session.Current))
            _session.Update(c => AndroidInputEdits.Rename(c, input.DeviceKey, input.Name, newName).Config);
        return true;
    }

    /// <summary>Only inputs no step picks: removing one in use would break that step.</summary>
    [RelayCommand]
    private void RemoveInput(AndroidInputViewModel input)
    {
        if (!CanEdit)
            return;
        if (AndroidInputEdits.Remove(_session.Current, input.DeviceKey, input.Name).Refusal == InputEditRefusal.None)
            _session.Update(c => AndroidInputEdits.Remove(c, input.DeviceKey, input.Name).Config);
    }

    [RelayCommand]
    private void AddUp(AndroidInputViewModel input) => SetMoves(input, [.. input.Moves.Select(m => m.Move), AndroidInputEdits.Up]);

    [RelayCommand]
    private void AddDown(AndroidInputViewModel input) => SetMoves(input, [.. input.Moves.Select(m => m.Move), AndroidInputEdits.Down]);

    /// <summary>× on a move: the others keep their order.</summary>
    [RelayCommand]
    private void RemoveMove(MoveToken move) =>
        SetMoves(move.Input, [.. move.Input.Moves.Where(m => m.Index != move.Index).Select(m => m.Move)]);

    private void SetMoves(AndroidInputViewModel input, IReadOnlyList<string> moves)
    {
        if (!CanEdit)
            return;
        if (AndroidInputEdits.SetMoves(_session.Current, input.DeviceKey, input.Name, moves).Refusal == InputEditRefusal.None)
            _session.Update(c => AndroidInputEdits.SetMoves(c, input.DeviceKey, input.Name, moves).Config);
    }

    private static string? InputError(InputEditRefusal refusal, string name, string device) => refusal switch
    {
        InputEditRefusal.Empty => "Give it a name.",
        InputEditRefusal.Taken => $"{device} already has an input called \"{name.Trim()}\".",
        InputEditRefusal.Missing => "It isn't in your Library anymore.",
        _ => null,
    };

    /// <summary>
    /// The inspector's "Add inputs in the Library" link: the Android section, with this device's inputs open.
    /// The shell shows the page.
    /// </summary>
    public void RevealInputs(string deviceKey)
    {
        var section = Section(LibraryKind.Android);
        SelectedIndex = Sections.ToList().IndexOf(section);
        foreach (var item in section.Items)
            item.ShowInputs = item.Key == deviceKey || item.ShowInputs;
    }

    // ---- Add, capture, remove ----

    /// <summary>Library › Add by hand (D-123): the section's form becomes a new item. False, with
    /// <see cref="LibrarySectionViewModel.ManualError"/> saying why, when a field is wrong or the device is already there.</summary>
    public bool AddByHand(LibraryKind kind)
    {
        var section = Section(kind);
        if (!CanEdit)
        {
            section.ManualError = "The Library can't be changed right now.";
            return false;
        }
        var flow = section.ManualFlowIndex == 1 ? AudioFlow.Input : AudioFlow.Output;
        var (library, _, error) = ManualItems.Add(_session.Current.Library, kind, section.ManualValue, section.ManualName, flow);
        section.ManualError = error;
        if (library is null)
            return false;
        section.Note = null;
        _session.Update(c => c with { Library = library });
        section.ManualValue = "";
        section.ManualName = "";
        section.ManualFlowIndex = 0;
        return true;
    }

    /// <summary>A device from Windows joins the Library under a key made from its name.</summary>
    [RelayCommand]
    private void Add(DeviceOption option)
    {
        var section = Sections.FirstOrDefault(s => s.Available.Contains(option));
        if (!CanEdit || !option.IsEnabled || section?.Kind is not { } kind || option.Device is not { } device)
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
        // A side of a bound device stays until that bound device goes (D-116): its steps would lose half the device.
        if (!CanEdit || kind is not { } k || UsedBy(_session.Current, k, item.Key) > 0 || _session.Current.Library.BoundWith(k, item.Key).Any())
            return;
        _session.Update(c => c with
        {
            Library = k switch
            {
                LibraryKind.Audio => c.Library with { Audio = [.. c.Library.Audio.Where(d => d.Key != item.Key)] },
                LibraryKind.Bluetooth => c.Library with { Bluetooth = [.. c.Library.Bluetooth.Where(d => d.Key != item.Key)] },
                LibraryKind.Display => c.Library with { Displays = [.. c.Library.Displays.Where(d => d.Key != item.Key)] },
                LibraryKind.Bound => c.Library with { Bound = [.. c.Library.Bound.Where(d => d.Key != item.Key)] },
                _ => c.Library with { Android = [.. c.Library.Android.Where(d => d.Key != item.Key)] },
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
