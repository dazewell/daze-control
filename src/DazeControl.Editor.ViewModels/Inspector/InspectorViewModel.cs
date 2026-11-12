using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DazeControl.Core.Catalog;
using DazeControl.Core.Input;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Inspector;

public enum InspectorMode
{
    /// <summary>Nothing selected: the flow's own settings (UX §5.1).</summary>
    Flow,
    Step,
    Card,

    /// <summary>Several nodes selected.</summary>
    Many,
}

/// <summary>A shortcut row in the flow's settings: the recorder's value, its note and its tip.</summary>
public sealed partial class ShortcutViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip), nameof(HasTip))]
    public partial string Hotkey { get; internal set; } = "";

    /// <summary>"Another app uses Alt+Z. Try another one." / "Alt+Z already runs flow "TV only"."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(Note), nameof(HasNote))]
    public partial string? Problem { get; internal set; }

    /// <summary>Why the last key pressed in this box was refused (UX §5.7 H1–H4, H7); cleared by the next recorded key.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note), nameof(HasNote))]
    public partial string? Rejection { get; internal set; }

    public bool HasProblem => Problem is not null;

    /// <summary>What shows under the box: the refused key's reason, else the shortcut's problem.</summary>
    public string? Note => Rejection ?? Problem;

    public bool HasNote => Note is not null;

    /// <summary>H8 for a number-pad key on its own.</summary>
    public string? Tip => ShortcutTexts.NumLockTip(Hotkey);

    public bool HasTip => Tip is not null;
}

/// <summary>A glyph in the flow's icon picker.</summary>
public sealed record GlyphOption(string Code, string Glyph, string Name);

/// <summary>
/// The right-hand pane of the flow editor (UX §5.4), built from the selection: a step's settings rendered from its
/// <see cref="ParamSpec"/>s, its failure policy (hover previews what wouldn't run) and timing, "Test this step";
/// a card's name and policy; with nothing selected, the flow's settings. Every change is one canvas edit, so it is
/// one undo step. UI thread only; engine answers come back through <c>post</c>.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject, IDisposable
{
    public const int MaxRetries = 10;
    public static readonly TimeSpan ResultTime = TimeSpan.FromSeconds(3);

    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private readonly Func<string, bool> _fileExists;
    private readonly DesktopShortcutStore _desktop;
    private readonly TimeProvider _time;
    private readonly Dictionary<LibraryKind, IReadOnlyList<DeviceInfo>> _live = [];

    /// <summary>The engine's lists behind <c>Suggest = Devices(kind)</c>: displays, services, credentials.</summary>
    private readonly Dictionary<string, IReadOnlyList<DeviceInfo>> _suggested = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _shortcutProblems = [];
    private string? _shortcutsFlowId; // whose rows Shortcuts holds, so a refusal's reason survives a refresh only there
    private CancellationTokenSource? _test;
    private bool _loading;
    private string? _stepType;

    public InspectorViewModel(ConfigSession session, FlowCanvasViewModel canvas, IEngineClient engine, Action<Action> post,
        Func<string, bool>? fileExists = null, DesktopShortcutStore? desktop = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _session = session;
        _canvas = canvas;
        _engine = engine;
        _post = post;
        _fileExists = fileExists ?? (p => File.Exists(p) || Directory.Exists(p));
        _desktop = desktop ?? new DesktopShortcutStore(engine, session.Path);
        _desktop.Changed += OnDesktopChanged;
        _session.PropertyChanged += OnSessionChanged;
        _canvas.PropertyChanged += OnCanvasChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.EventReceived += OnEngineEvent;
        Load();
        UpdateDesktop();
        _ = RefreshDesktopAsync();
    }

    public string FlowId => _canvas.FlowId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlow), nameof(IsStep), nameof(IsCard), nameof(IsMany), nameof(ManyHint), nameof(IsNode))]
    public partial InspectorMode Mode { get; private set; }

    public bool IsFlow => Mode == InspectorMode.Flow;

    public bool IsStep => Mode == InspectorMode.Step;

    public bool IsCard => Mode == InspectorMode.Card;

    public bool IsMany => Mode == InspectorMode.Many;

    /// <summary>A step or a card: the header, name and policy apply.</summary>
    public bool IsNode => IsStep || IsCard;

    /// <summary>False while the file can't be written or a run is on the canvas (UX §5.5).</summary>
    public bool CanEdit => _canvas.CanEdit;

    /// <summary>The selected step or card.</summary>
    [ObservableProperty]
    public partial string? NodeId { get; private set; }

    [ObservableProperty]
    public partial string Glyph { get; private set; } = "";

    /// <summary>"Bluetooth › Connect"; cards "Runs together · 2 steps".</summary>
    [ObservableProperty]
    public partial string Kind { get; private set; } = "";

    /// <summary>The node's own name; "" = the provider's title, shown as <see cref="NamePlaceholder"/>.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string NamePlaceholder { get; private set; } = "";

    /// <summary>"3 steps selected".</summary>
    [ObservableProperty]
    public partial string ManyText { get; private set; } = "";

    public ObservableCollection<ParamFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    public partial bool HasFields { get; private set; }

    // ---- If it fails (UX §5.4) ----

    public static string Describe(FailurePolicy policy) => policy switch
    {
        FailurePolicy.Skip => "If this fails, the steps after it are skipped. Steps on other paths carry on. Only an \"When it works\" follow-up is skipped; an \"Only if it fails\" step runs.",
        FailurePolicy.Ignore => "If this fails, the steps after it still run, including the \"When it works\" ones, as if it had worked. Use it only when failure doesn't matter.",
        _ => "If this fails, the whole flow stops.",
    };

    /// <summary>0 Stop, 1 Skip, 2 Run the next steps anyway (RadioButtons' SelectedIndex).</summary>
    [ObservableProperty]
    public partial int PolicyIndex { get; set; }

    // ---- Timing ----

    /// <summary>"Try again" 0–10.</summary>
    [ObservableProperty]
    public partial double Retries { get; set; }

    /// <summary>"Wait between tries", seconds.</summary>
    [ObservableProperty]
    public partial double RetryDelay { get; set; }

    /// <summary>"Give up after", seconds; NaN = the step type's default.</summary>
    [ObservableProperty]
    public partial double Timeout { get; set; } = double.NaN;

    /// <summary>"10 s" — what an empty "Give up after" means.</summary>
    [ObservableProperty]
    public partial string TimeoutPlaceholder { get; private set; } = "";

    public bool CanRetryDelay => Retries > 0;

    // ---- Test this step ----

    public bool EngineRunning => _engine.IsRunning;

    public bool CanTest => IsStep && EngineRunning && !IsTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest), nameof(IsIdle))]
    public partial bool IsTesting { get; private set; }

    public bool IsIdle => !IsTesting;

    /// <summary>UX #42 "You picked Couch TV. Testing doesn't run it." / #44 "Nothing picked".</summary>
    /// <param name="result">The step's result.</param>
    /// <param name="summary">"Picked Couch TV": <c>NodeResult.Summary</c> once T12.4 lands it (null until then).</param>
    public static string ExplainMenu(NodeResult result, string? summary = null) => result.Outcome switch
    {
        NodeOutcome.Succeeded when summary?.StartsWith("Picked ", StringComparison.Ordinal) == true =>
            $"You picked {summary["Picked ".Length..]}. Testing doesn't run it.",
        NodeOutcome.Succeeded => "Worked. Testing doesn't run the choice.",
        NodeOutcome.Cancelled => "Nothing picked.",
        _ => Explain(result),
    };

    /// <summary>"Worked in 1.2 s." / "Didn't work: …"; null = no result to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult), nameof(ShowTestSuccess), nameof(ShowTestFailure))]
    public partial string? TestResult { get; private set; }

    public bool HasTestResult => TestResult is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTestSuccess), nameof(ShowTestFailure))]
    public partial bool TestSucceeded { get; private set; }

    public bool ShowTestSuccess => HasTestResult && TestSucceeded;

    public bool ShowTestFailure => HasTestResult && !TestSucceeded;

    /// <summary>The step's error in full for "Show details"; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestDetails))]
    public partial string? TestDetails { get; private set; }

    public bool HasTestDetails => TestDetails is not null;

    // ---- Flow settings (nothing selected) ----

    [ObservableProperty]
    public partial string FlowName { get; set; } = "";

    [ObservableProperty]
    public partial string FlowGlyph { get; private set; } = "";

    public IReadOnlyList<GlyphOption> GlyphOptions { get; } = BuildGlyphs();

    /// <summary>Tray submenu ("" = top level); the editable ComboBox commits on leaving it.</summary>
    [ObservableProperty]
    public partial string TrayFolder { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> Folders { get; private set; } = [];

    [ObservableProperty]
    public partial bool LineAbove { get; set; }

    public ObservableCollection<ShortcutViewModel> Shortcuts { get; } = [];

    /// <summary>"Test this step", or "Show the menu" for a menu (DESIGN §3.7).</summary>
    public string TestLabel => Node is StepNode { Step: ProviderCatalog.MenuShowId } ? "Show the menu" : "Test this step";

    public string TestTip => Node is StepNode { Step: ProviderCatalog.MenuShowId }
        ? "Shows the menu on screen. Picking a choice doesn't run it"
        : "Runs just this step now, with the values shown here";

    /// <summary>"If it's already running": 0 Ignore it, 1 Run it after, 2 Start over.</summary>
    [ObservableProperty]
    public partial int WhenRunningIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowPopup { get; set; }

    // ---- Desktop shortcut (flow settings, D-049, UX §5.4) ----

    /// <summary>"Puts a shortcut…" (#23), "On your desktop", "On your desktop as “Old name”", or #25 without the engine.</summary>
    [ObservableProperty]
    public partial string DesktopStatus { get; private set; } = DesktopShortcutStore.AddHelp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddToDesktop))]
    public partial bool HasDesktopShortcut { get; private set; }

    public bool ShowAddToDesktop => !HasDesktopShortcut;

    [ObservableProperty]
    public partial bool CanAddToDesktop { get; private set; }

    /// <summary>The shortcut still has the flow's old name: "Update" renames it.</summary>
    [ObservableProperty]
    public partial bool CanUpdateDesktop { get; private set; }

    [ObservableProperty]
    public partial bool CanRemoveFromDesktop { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDesktopError))]
    public partial string? DesktopError { get; private set; }

    public bool HasDesktopError => DesktopError is not null;

    [RelayCommand]
    private Task AddToDesktopAsync() => ChangeDesktopAsync(() => _desktop.AddAsync(FlowId));

    [RelayCommand]
    private Task RemoveFromDesktopAsync() => ChangeDesktopAsync(() => _desktop.RemoveAsync(FlowId));

    private async Task ChangeDesktopAsync(Func<Task<string?>> change)
    {
        DesktopError = null;
        var error = await change().ConfigureAwait(false);
        _post(() => DesktopError = error);
    }

    private async Task RefreshDesktopAsync()
    {
        try
        {
            await _desktop.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // The engine went away mid-request: StateChanged follows and shows #25.
        }
    }

    private void OnDesktopChanged() => _post(UpdateDesktop);

    private void UpdateDesktop()
    {
        var running = _desktop.EngineRunning;
        var shortcut = _desktop.Of(FlowId);
        HasDesktopShortcut = shortcut is not null;
        CanAddToDesktop = running && shortcut is null;
        CanUpdateDesktop = running && shortcut is { CurrentName: false };
        CanRemoveFromDesktop = running && shortcut is not null;
        DesktopStatus = !running ? DesktopShortcutStore.EngineOffText
            : shortcut is null ? DesktopShortcutStore.AddHelp
            : shortcut.CurrentName ? "On your desktop"
            : $"On your desktop as “{shortcut.Name}”";
    }

    private Flow? Flow => _canvas.Flow;

    private Node? Node => NodeId is null ? null : Flow?.FindNode(NodeId);

    // ---- Loading ----

    /// <summary>Re-reads the selection and the config; values the user didn't change stay as they are.</summary>
    private void Load()
    {
        OnPropertyChanged(nameof(TestLabel));
        OnPropertyChanged(nameof(TestTip));
        _loading = true;
        try
        {
            var selected = _canvas.SelectedIds;
            var flow = Flow;
            Node? node = selected.Count == 1 ? flow?.FindNode(selected.First()) : null;
            var mode = selected.Count > 1 ? InspectorMode.Many
                : node is StepNode ? InspectorMode.Step
                : node is GroupNode ? InspectorMode.Card
                : InspectorMode.Flow;
            if (node?.Id != NodeId)
            {
                InputTip = null;
                ClearTest();
                _canvas.Preview(null);
            }
            NodeId = node?.Id;
            Mode = mode;
            OnPropertyChanged(nameof(CanTest));
            ManyText = string.Create(CultureInfo.InvariantCulture, $"{selected.Count} selected");
            if (mode == InspectorMode.Many)
                LoadMany(selected);
            else
            {
                _bulk = null;
                ClearManyFields();
            }
            if (node is not StepNode { Step: ProviderCatalog.RunFlowId })
                CalledFlowId = null;
            if (node is not StepNode)
                ClearOnlyIf();
            switch (node)
            {
                case StepNode step:
                    LoadStep(step);
                    LoadGivenValues(step);
                    break;
                case GroupNode card:
                    LoadCard(card);
                    LoadGivenValues(null);
                    break;
                default:
                    LoadGivenValues(null);
                    ClearFields();
                    if (flow is not null)
                        LoadFlow(flow);
                    break;
            }
            LoadConnections();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadStep(StepNode step)
    {
        var descriptor = ProviderCatalog.Find(step.Step);
        Glyph = FlowItemViewModel.DecodeGlyph(descriptor?.Glyph ?? "E9CE");
        Kind = descriptor is null ? step.Step : $"{CanvasNodeViewModel.Group(descriptor.Area)} › {descriptor.Title}";
        // A call's pill is named after the flow it runs; the name box shows the same (UX §5.4).
        NamePlaceholder = step.Step == ProviderCatalog.RunFlowId && ConfigValidator.CalledFlow(step) is { Length: > 0 } called
            && _session.Current.Flows.FirstOrDefault(f => f.Id == called) is { } target
            ? "Run " + target.Name
            : descriptor?.Title ?? step.Step;
        Name = step.Name ?? "";
        LoadTiming(step, descriptor);
        LoadOnlyIf(step, descriptor);
        OnPropertyChanged(nameof(CanRetryDelay));

        if (step.Step != _stepType || Fields.Count == 0 && descriptor?.Params.Count > 0)
        {
            ClearFields();
            _stepType = step.Step;
            foreach (var spec in descriptor?.Params ?? [])
            {
                var field = new ParamFieldViewModel(spec, WriteParam, _fileExists)
                {
                    IsCallInputs = step.Step == ProviderCatalog.RunFlowId && spec.Name == FlowInputs.Param,
                };
                field.DevicePicked += OnDevicePicked;
                Fields.Add(field);
            }
            HasFields = Fields.Count > 0;
            foreach (var kind in Fields.Where(f => f.IsDevice).Select(f => f.Spec.Library).OfType<LibraryKind>().Distinct())
                _ = RefreshDevicesAsync(kind);
            foreach (var kind in Fields.Select(f => f.Spec.Suggest).Where(s => s?.Source == SuggestSource.Devices && s.Kind != ParamSuggest.AndroidApps).Select(s => s!.Kind).Distinct())
                _ = RefreshSuggestionsAsync(kind);
        }
        var path = $"flows.{FlowId}.nodes.{step.Id}.with.";
        foreach (var field in Fields)
        {
            step.With.TryGetValue(field.Name, out var value);
            if (field.IsDevice && field.Spec.Library is { } kind)
                field.SetDevices(Options(field, kind, (value as ScalarParam)?.Text), (value as ScalarParam)?.Text);
            else if (field.IsKindDevice)
                SetKindDevices(field, step, (value as ScalarParam)?.Text);
            var errors = _session.Problems.Where(p => p.Severity == Severity.Error).ToList();
            var problem = errors.FirstOrDefault(p => p.Path == path + field.Name)?.Message
                // A missing required setting is reported on "with" as "Device is missing." (UX §5.7 #9).
                ?? (errors.Any(p => p.Path == path[..^1] && p.Message == $"{field.Spec.Title} is missing.")
                    ? field.IsFlowRef ? "Choose a flow." : field.IsDevice ? "Choose a device." : "Fill this in." : null);
            field.SetValueContext(_session.Current.Flows.FirstOrDefault(f => f.Id == FlowId), step.Id);
            field.Load(value, problem);
            Suggest(field, step);
            if (field.IsCallInputs)
                LoadCallInputs(field, step);
        }
    }

    /// <summary>Fills a field's suggestions from what's known now: the engine's last list or the open config.</summary>
    private void Suggest(ParamFieldViewModel field, StepNode step)
    {
        switch (field.Spec.Suggest)
        {
            case { Source: SuggestSource.Devices, Kind: ParamSuggest.AndroidApps }:
                LoadApps(field, step);
                break;
            case { Source: SuggestSource.Devices, Kind: var kind } when field.IsChecklist:
                field.SetScreens(_suggested.GetValueOrDefault(kind));
                break;
            case { Source: SuggestSource.Devices, Kind: var kind }:
                field.SetSuggestions(_suggested.GetValueOrDefault(kind)?.Select(d => new SuggestionItem(d.Value ?? d.Name, Caption(d))) ?? [],
                    "");
                break;
            case { Source: SuggestSource.Config, Kind: ParamSuggest.Flows } when field.IsFlowList:
                field.SetFlowContext(_session.Current, _session.Problems, FlowId);
                break;
            case { Source: SuggestSource.Config, Kind: ParamSuggest.Flows }:
                // Trimmed like ConfigValidator.CalledFlow and the runner, so a padded id still finds its flow.
                var target = step.With.TryGetValue(field.Name, out var t) ? (t as ScalarParam)?.Text.Trim() : null;
                var flows = FlowOptions(_session.Current, _session.Problems, FlowId, target);
                field.SetDevices(flows, target);
                field.SetSuggestionHint(flows.Any(r => r.Source == DeviceSource.Library && r.Key != FlowId) ? "" : NoOtherFlow);
                break;
            case { Source: SuggestSource.Config, Kind: ParamSuggest.Flags }:
                field.SetSuggestions(FlagNames(_session.Current), "");
                break;
            case { Source: SuggestSource.Config, Kind: ParamSuggest.AndroidInputs }:
                var device = AndroidDeviceOf(step);
                field.SetSuggestions(device?.Inputs.Keys.Select(i => new SuggestionItem(i, "")) ?? [],
                    device is null ? "Choose the Android device first." : AddInputsHint, device?.Key);
                break;
        }
    }

    /// <summary>Android apps by device serial, as the engine last answered (D-093).</summary>
    private readonly Dictionary<string, List<AppItem>> _apps = new(StringComparer.Ordinal);

    /// <summary>Fills the app picker from what is known; asks the engine for its saved list (never the device) when nothing is.</summary>
    private void LoadApps(ParamFieldViewModel field, StepNode step)
    {
        var device = AndroidDeviceOf(step);
        if (device is null)
        {
            field.SetApps(null, [], "Choose the Android device first.");
            return;
        }
        field.SetApps(device, _apps.GetValueOrDefault(device.Serial) ?? [], "");
        if (!_apps.ContainsKey(device.Serial))
            _ = RefreshAppsAsync(field, refresh: false);
    }

    /// <summary>
    /// Asks the engine for the device's apps: its saved list, or with <paramref name="refresh"/> the device itself (D-093;
    /// the box asks once when it gets focus). A failure keeps the old list and says so; typing a package still works.
    /// </summary>
    public async Task RefreshAppsAsync(ParamFieldViewModel field, bool refresh)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.AppsDevice is not { } device || (refresh && field.AppsRefreshed))
            return;
        field.AppsRefreshed |= refresh;
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.AndroidApps, Address = device.Address, Adb = device.Adb, Refresh = refresh })
            .ConfigureAwait(false);
        _post(() =>
        {
            if (field.AppsDevice?.Serial != device.Serial)
                return; // another device was chosen meanwhile
            if (answer is { Ok: true, Devices: { } devices })
            {
                var apps = AppChoices.From(devices);
                _apps[device.Serial] = apps;
                field.SetApps(device, apps, apps.Count == 0 && refresh ? "No apps found on the device. Type the package name." : "");
            }
            else if (refresh)
                field.SetApps(device, _apps.GetValueOrDefault(device.Serial) ?? [],
                    $"Couldn't read the apps: {answer?.Error ?? "the engine isn't running"}. Type the package name.");
        });
    }

    /// <summary>The Android input hint; a link that opens the device's inputs in the Library (L1).</summary>
    public const string AddInputsHint = "Add inputs in the Library.";

    /// <summary>The step's Android device: by key, or through this flow's input (<c>$tv</c>) to its default.</summary>
    private AndroidDevice? AndroidDeviceOf(StepNode step)
    {
        if (!step.With.TryGetValue("device", out var value))
            return null;
        var key = FlowInputs.NameIn(value) is { } own
            ? _session.Current.Flows.FirstOrDefault(f => f.Id == FlowId)?.Inputs.GetValueOrDefault(own) is { Kind: LibraryKind.Android } input
                ? input.Default
                : null
            : (value as ScalarParam)?.Text;
        return key is null ? null : _session.Current.Library.Android.FirstOrDefault(d => d.Key == key);
    }

    /// <summary>"Add inputs in the Library" › the shell shows Library › Android with this device's inputs open.</summary>
    public event Action<string>? AndroidInputsRequested;

    public void OpenAndroidInputs(ParamFieldViewModel field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.HintDevice is { } device)
            AndroidInputsRequested?.Invoke(device);
    }

    /// <summary>"Print Spooler · Running", "Saved in Windows".</summary>
    private static string Caption(DeviceInfo device)
    {
        var state = device.State is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : null;
        string?[] parts = [device.Detail == (device.Value ?? device.Name) ? null : device.Detail, state];
        return Captions.Join(parts);
    }

    /// <summary>UX microcopy #69: the flow picker with no other flow.</summary>
    public const string NoOtherFlow = "Make another flow first. This step runs one of your other flows.";

    /// <summary>
    /// The flow picker's rows (UX §5.4, F2): the other flows by name with #29 captions, storing the id; a target that
    /// isn't a flow anymore stays as a row of its own so nothing changes by itself.
    /// </summary>
    internal static List<DeviceOption> FlowOptions(Configuration config, IReadOnlyList<Problem> problems, string selfId, string? current)
    {
        DeviceOption Row(Flow flow, IReadOnlyList<Problem> problems)
        {
            var loop = ConfigValidator.CallProblem(config.Flows, selfId, flow.Id);
            return new(DeviceSource.Library, flow.Id, flow.Name,
                loop ?? (!flow.Enabled ? "Turned off"
                    : problems.Any(p => p.Severity == Severity.Error && p.FlowId == flow.Id) ? "Needs fixing"
                    : Takes(flow)),
                Core.Model.FlowGlyph.Decode(Core.Model.FlowGlyph.CodeOf(flow)))
            {
                IsEnabled = loop is null, // #49 / #50: listed, but off
            };
        }
        // Flows it can run first, then the ones that would make a loop, then its own flow.
        var rows = config.Flows
            .Select(f => Row(f, problems))
            .OrderBy(r => !r.IsEnabled)
            .ThenBy(r => r.Key == selfId)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (current is { Length: > 0 } && rows.All(r => r.Key != current))
            rows.Add(config.Flows.FirstOrDefault(f => f.Id == current) is { } self
                ? Row(self, problems)
                : new(DeviceSource.Missing, current, current, FlowGone, Core.Model.FlowGlyph.Decode("E8FD")));
        return rows;
    }

    /// <summary>The caption of a flow picker row whose flow was deleted (#51 says the rest under the field).</summary>
    public const string FlowGone = "Isn't there anymore";

    /// <summary>Flag names the config's steps use, with "Used in 2 flows".</summary>
    internal static List<SuggestionItem> FlagNames(Configuration config) =>
    [
        .. config.Flows
            .SelectMany(f => f.Nodes.OfType<StepNode>()
                .Where(n => n.Step.StartsWith("flag.", StringComparison.Ordinal) && n.With.TryGetValue("name", out var v) && v is ScalarParam)
                .Select(n => (Flow: f.Id, Name: ((ScalarParam)n.With["name"]).Text)))
            .Where(x => x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (Name: g.Key, Flows: g.Select(x => x.Flow).Distinct().Count()))
            .Select(f => new SuggestionItem(f.Name, f.Flows == 1 ? "Used in 1 flow" : $"Used in {f.Flows} flows")),
    ];

    /// <summary>Asks the engine for a suggestion list (on load and each time a drop-down opens).</summary>
    /// <summary>#27 from what's on now, not the last list; no change when the engine doesn't answer.</summary>
    public async Task UseScreensOnNowAsync(ParamFieldViewModel field)
    {
        if (field.Spec.Suggest is { Source: SuggestSource.Devices, Kind: var kind } && await RefreshSuggestionsAsync(kind).ConfigureAwait(false))
            _post(field.UseScreensOnNow); // queued after the refresh's own update
    }

    public async Task<bool> RefreshSuggestionsAsync(string kind)
    {
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Devices, Kind = kind }).ConfigureAwait(false);
        if (answer is not { Ok: true, Devices: { } devices })
            return false;
        _post(() =>
        {
            _suggested[kind] = devices;
            if (Node is not StepNode step)
                return;
            _loading = true;
            try
            {
                foreach (var field in Fields.Where(f => f.Spec.Suggest is { Source: SuggestSource.Devices } s && s.Kind == kind))
                    Suggest(field, step);
            }
            finally
            {
                _loading = false;
            }
        });
        return true;
    }

    private void LoadCard(GroupNode card)
    {
        ClearFields();
        Glyph = FlowItemViewModel.DecodeGlyph("E8FD");
        Kind = card.Members.Count == 1 ? "Runs together · 1 step" : $"Runs together · {card.Members.Count} steps";
        NamePlaceholder = "Run together";
        Name = card.Name ?? "";
        PolicyIndex = (int)(card.OnFailure ?? FailurePolicy.Stop);
    }

    private string? _foldersFlowId;

    private void SetFolders(Flow flow, bool includeOwn)
    {
        var folders = _session.Current.Flows.Where(f => includeOwn || f.Id != flow.Id).Select(f => f.Tray.Folder).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture).ToList();
        if (!folders.SequenceEqual(Folders))
            Folders = folders;
    }

    /// <summary>The Group box lost focus: its text is a choice now, so the list holds it and the box keeps showing it.</summary>
    public void CommitFolder()
    {
        if (_canvas.Flow is { } flow)
            SetFolders(flow, includeOwn: true);
    }

    private void LoadFlow(Flow flow)
    {
        FlowName = flow.Name;
        FlowGlyph = FlowItemViewModel.DecodeGlyph(flow.Glyph ?? FirstGlyph(flow));
        // Typing in the editable Tray folder box saves each key and lands here: rewriting its text (a trimmed
        // "My " loses the space) or swapping its item list resets the box, drops focus and selects all (issue #74).
        if (TrayFolder.Trim() != (flow.Tray.Folder ?? ""))
            TrayFolder = flow.Tray.Folder ?? "";
        LineAbove = flow.Tray.Separator;
        // Other flows' folders only while typing: this flow's own text would grow the list with every typed letter.
        // A different flow (or the box losing focus: CommitFolder) lists its own folder too, so the box can show it.
        var own = _foldersFlowId != flow.Id;
        _foldersFlowId = flow.Id;
        SetFolders(flow, includeOwn: own);
        WhenRunningIndex = (int)flow.WhenRunning;
        ShowPopup = flow.Popup;
        LoadInputs(flow);
        LoadTriggers(flow);

        var pending = Shortcuts.Where(s => s.Hotkey.Length == 0).ToList();
        // A refused key's reason lives until the next recorded key, not until the next refresh (same row, same value).
        var rejections = flow.Id == _shortcutsFlowId
            ? Shortcuts.Where(s => s.Rejection is not null).ToDictionary(s => (s.Index, s.Hotkey), s => s.Rejection)
            : [];
        _shortcutsFlowId = flow.Id;
        Shortcuts.Clear();
        var problems = _session.Problems.Where(p => p.Path == $"flows.{flow.Id}.hotkeys").Select(p => p.Message).ToList();
        for (var i = 0; i < flow.Hotkeys.Count; i++)
        {
            var text = Hotkey.TryParse(flow.Hotkeys[i], out var parsed) ? parsed.ToString() : flow.Hotkeys[i];
            var busy = _shortcutProblems.FirstOrDefault(m => m.StartsWith(text + ": ", StringComparison.Ordinal));
            Shortcuts.Add(new ShortcutViewModel(i)
            {
                Hotkey = text,
                Problem = problems.FirstOrDefault(m => m.StartsWith(text + " ", StringComparison.Ordinal)) ?? busy?[(text.Length + 2)..],
                Rejection = rejections.GetValueOrDefault((i, text)),
            });
        }
        if (pending.Count > 0)
            Shortcuts.Add(new ShortcutViewModel(flow.Hotkeys.Count) { Rejection = rejections.GetValueOrDefault((flow.Hotkeys.Count, "")) });
    }

    private void ClearFields()
    {
        foreach (var field in Fields)
            field.DevicePicked -= OnDevicePicked;
        Fields.Clear();
        HasFields = false;
        _stepType = null;
    }

    private List<DeviceOption> Options(ParamFieldViewModel field, LibraryKind kind, string? current) =>
        LibraryDevices.Options(kind, _session.Current.Library, _live.GetValueOrDefault(kind), current, field.Spec.Required,
            canCapture: EngineRunning && CanEdit, Flow?.Inputs, picks: PicksBefore());

    /// <summary>
    /// Pick's Default (D-128): the Library items of the step's kind, stored as the key. A name typed by hand in an older flow
    /// shows as its item when exactly one matches; it is rewritten only when the person picks something.
    /// </summary>
    private void SetKindDevices(ParamFieldViewModel field, StepNode step, string? current)
    {
        var library = _session.Current.Library;
        var kind = PickResults.KindOf(step);
        field.ShownKey = kind is { } k ? text => PickResults.DefaultKey(library, k, text) : null;
        List<DeviceOption> rows = [new(DeviceSource.None, null, "No default", "", LibraryDevices.Glyph(kind ?? LibraryKind.Audio))];
        if (kind is { } known)
            rows.AddRange(LibraryDevices.Options(known, library, null, field.ShownKey?.Invoke(current) ?? current, required: true, canCapture: false));
        else if (!string.IsNullOrEmpty(current))
            rows.Add(new(DeviceSource.Missing, current, current, LibraryDevices.NotFound, LibraryDevices.Glyph(LibraryKind.Audio)));
        field.SetDevices(rows, current);
    }

    /// <summary>The Pick steps that finish before the selected step (D-117): their results can fill its device settings.</summary>
    private IReadOnlyList<StepNode> PicksBefore() =>
        Mode != InspectorMode.Many && Flow is { } flow && Node is StepNode step ? PickResults.Before(flow, step.Id) : [];

    /// <summary>Asks the engine what's on this PC now (on load and each time a picker opens, UX §5.4).</summary>
    public async Task RefreshDevicesAsync(LibraryKind kind)
    {
        if (kind is LibraryKind.Display or LibraryKind.Bound)
            return; // layouts are captured, bound devices made in the Library: neither is listed from Windows
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Devices, Kind = LibraryDevices.PipeKind(kind) }).ConfigureAwait(false);
        if (answer is not { Ok: true, Devices: { } devices })
            return;
        _post(() =>
        {
            _live[kind] = devices;
            if (Mode == InspectorMode.Flow && Flow is { } flow)
                LoadInputs(flow);
            if (Mode == InspectorMode.Many && _bulk is { } bulk)
            {
                _loading = true;
                try
                {
                    foreach (var field in ManyFields.Where(f => f.Spec.Library == kind))
                        ShowMany(field, bulk.Param(field.Name)!);
                }
                finally
                {
                    _loading = false;
                }
            }
            if (Node is StepNode step)
            {
                _loading = true;
                try
                {
                    foreach (var field in Fields.Where(f => f.Spec.Library == kind))
                    {
                        var current = step.With.TryGetValue(field.Name, out var v) ? (v as ScalarParam)?.Text : null;
                        field.SetDevices(Options(field, kind, current), current);
                    }
                    foreach (var field in Fields.Where(f => f.IsCallInputs))
                        LoadCallInputs(field, step);
                }
                finally
                {
                    _loading = false;
                }
            }
        });
    }

    // ---- Writing ----

    private void EditNode(Func<Node, Node> change)
    {
        if (_loading || NodeId is not { } id || !CanEdit)
            return;
        _canvas.Edit(flow => flow with { Nodes = [.. flow.Nodes.Select(n => n.Id == id ? change(n) : n)] });
    }

    private void EditStep(Func<StepNode, StepNode> change) => EditNode(n => n is StepNode s ? change(s) : n);

    private void EditFlow(Func<Flow, Flow> change)
    {
        if (!_loading && CanEdit)
            _canvas.Edit(change);
    }

    private void WriteParam(ParamFieldViewModel field, ParamValue? value)
    {
        if (Mode == InspectorMode.Many)
        {
            WriteManyParam(field, value);
            return;
        }
        // An editable ComboBox pushes its text back on focus: the same value must not make the flow dirty.
        if (Node is StepNode now && Equals(now.With.TryGetValue(field.Name, out var old) ? old : null, value))
            return;
        WriteChanged(field, value);
    }

    private void WriteChanged(ParamFieldViewModel field, ParamValue? value) => EditStep(s =>
    {
        var written = s with
        {
            With = value is null
                ? new(s.With.Where(e => e.Key != field.Name))
                : new([.. s.With, new KeyValuePair<string, ParamValue>(field.Name, value)]),
        };
        if (s.Step == ProviderCatalog.PromptPickId && field.Name == "kind")
            written = DropStaleDefault(written);
        return field.IsFlowRef && s.Step == ProviderCatalog.RunFlowId ? KeepPassedInputs(written, s, _session.Current) : written;
    });

    /// <summary>Changing a Pick's kind clears a default that isn't an item of the new kind (D-128); one that still fits stays.</summary>
    private StepNode DropStaleDefault(StepNode pick) =>
        pick.With.TryGetValue("default", out var value) && value is ScalarParam { Text.Length: > 0 } text
        && (PickResults.KindOf(pick) is not { } kind || PickResults.DefaultKey(_session.Current.Library, kind, text.Text) is null)
            ? pick with { With = new(pick.With.Where(e => e.Key != "default")) }
            : pick;

    /// <summary>A device from Windows joins the Library under a new key; a capture becomes a new layout.</summary>
    private void OnDevicePicked(ParamFieldViewModel field, DeviceOption option)
    {
        if (field.Spec.Library is not { } kind || !CanEdit)
            return;
        if (option.Source == DeviceSource.Capture)
        {
            _ = CaptureLayoutAsync(field);
            return;
        }
        if (option.Device is not { } device)
            return;
        var (library, key) = LibraryDevices.Add(_session.Current.Library, kind, device);
        _session.Update(c => c with { Library = library });
        WriteParam(field, new ScalarParam(key));
    }

    private async Task CaptureLayoutAsync(ParamFieldViewModel field)
    {
        var key = LibraryDevices.NewKey(_session.Current.Library, LibraryKind.Display, "layout");
        var nodeId = NodeId;
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.CaptureDisplayLayout, Key = key }).ConfigureAwait(false);
        _post(() =>
        {
            if (NodeId != nodeId || !Fields.Contains(field))
                return; // the selection moved on while the screens were read

            var captured = answer is { Ok: true, Yaml: { } yaml } ? ConfigYaml.Load(yaml).Config?.Library.Displays is { Count: > 0 } displays ? displays[0] : null : null;
            if (captured is null)
            {
                Load(); // back to the key the step had
                return;
            }
            _session.Update(c => c with { Library = c.Library with { Displays = [.. c.Library.Displays, captured] } });
            WriteParam(field, new ScalarParam(captured.Key));
        });
    }

    partial void OnNameChanged(string value)
    {
        var name = value.Trim();
        EditNode(n => n with { Name = name.Length == 0 ? null : name });
    }

    partial void OnPolicyIndexChanged(int value)
    {
        if (value is < 0 or > 2)
            return;
        // Stop is stored as "not set" unless the type's default says otherwise: then it must stay explicit (DESIGN A3).
        var policy = (FailurePolicy)value;
        EditNode(n => n with
        {
            OnFailure = policy == FailurePolicy.Stop && (n is not StepNode s || TypeDefault(s.Step)?.OnFailure is null) ? null : policy,
        });
    }

    partial void OnRetriesChanged(double value)
    {
        OnPropertyChanged(nameof(CanRetryDelay));
        var count = double.IsNaN(value) ? 0 : (int)Math.Clamp(Math.Round(value), 0, MaxRetries);
        EditStep(s =>
        {
            var inherited = TypeDefault(s.Step);
            return s with
            {
                RetryCount = count == 0 && inherited?.RetryCount is null ? null : count,
                // A first retry waits 1 s unless the type's default has a delay of its own.
                RetryDelay = count == 0 ? null : s.RetryDelay ?? (inherited?.RetryDelay is null ? TimeSpan.FromSeconds(1) : null),
            };
        });
    }

    partial void OnRetryDelayChanged(double value)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, double.IsNaN(value) ? 0 : value) * 1000));
        EditStep(s => (s.RetryCount ?? TypeDefault(s.Step)?.RetryCount) is > 0 ? s with { RetryDelay = delay } : s);
    }

    partial void OnTimeoutChanged(double value) => EditStep(s => s with
    {
        Timeout = double.IsNaN(value) || value <= 0 ? null : TimeSpan.FromMilliseconds(Math.Round(value * 1000)),
    });

    partial void OnFlowNameChanged(string value)
    {
        var name = value.Trim();
        if (name.Length > 0)
            EditFlow(f => f with { Name = name });
    }

    [RelayCommand]
    private void PickGlyph(GlyphOption option) =>
        EditFlow(f => f with { Glyph = f.Glyph is null && option.Code == FirstGlyph(f) ? null : option.Code });

    partial void OnTrayFolderChanged(string value)
    {
        var folder = value.Trim();
        EditFlow(f => f with { Tray = f.Tray with { Folder = folder.Length == 0 ? null : folder } });
    }

    partial void OnLineAboveChanged(bool value) => EditFlow(f => f with { Tray = f.Tray with { Separator = value } });

    partial void OnWhenRunningIndexChanged(int value)
    {
        if (value is >= 0 and <= 2)
            EditFlow(f => f with { WhenRunning = (WhenRunning)value });
    }

    partial void OnShowPopupChanged(bool value) => EditFlow(f => f with { Popup = value });

    /// <summary>Adds an empty recorder; nothing is written until a chord is recorded in it.</summary>
    [RelayCommand]
    private void AddShortcut()
    {
        if (Shortcuts.All(s => s.Hotkey.Length > 0))
            Shortcuts.Add(new ShortcutViewModel(Shortcuts.Count));
    }

    /// <summary>The recorder at <paramref name="index"/> recorded <paramref name="hotkey"/> ("" = cleared: removed).</summary>
    public void SetShortcut(int index, string hotkey)
    {
        if (Flow is not { } flow)
            return;
        if (index < Shortcuts.Count)
            Shortcuts[index].Rejection = null; // also when the same chord comes again and nothing reloads
        var list = flow.Hotkeys.ToList();
        if (hotkey.Length == 0)
        {
            if (index < list.Count)
                list.RemoveAt(index);
            else if (Shortcuts.LastOrDefault() is { Hotkey.Length: 0 } pending)
                Shortcuts.Remove(pending);
        }
        else if (index < list.Count)
        {
            list[index] = hotkey;
        }
        else
        {
            list.Add(hotkey);
        }
        if (!list.SequenceEqual(flow.Hotkeys))
        {
            if (index >= flow.Hotkeys.Count && Shortcuts.LastOrDefault() is { Hotkey.Length: 0 } pending)
                Shortcuts.Remove(pending);
            EditFlow(f => f with { Hotkeys = [.. list] });
        }
    }

    /// <summary>The recorder at <paramref name="index"/> refused a key: say why under it (UX §5.7 H1–H4, H7). Nothing is written.</summary>
    public void RejectShortcut(int index, HotkeyRejection reason)
    {
        if (index < Shortcuts.Count)
            Shortcuts[index].Rejection = ShortcutTexts.Rejection(reason);
    }

    /// <summary>Typed text at <paramref name="index"/> couldn't be read as a shortcut (T14.12): say why under the box.</summary>
    public void RejectShortcutText(int index, string text)
    {
        if (index < Shortcuts.Count)
            Shortcuts[index].Rejection = text;
    }

    [RelayCommand]
    private void RemoveShortcut(ShortcutViewModel shortcut) => SetShortcut(shortcut.Index, "");

    /// <summary>
    /// A shortcut box got or lost focus: the engine lets go of its shortcuts meanwhile, so the box can record one the
    /// engine owns (D-072). When the editor quits, its connection goes and the engine takes them back by itself.
    /// </summary>
    public void SetRecordingShortcut(bool recording)
    {
        _recordingShortcut = recording;
        _ = _engine.SendAsync(new PipeRequest { Op = Ops.RecordHotkey, On = recording });
    }

    // A restarted engine knows nothing of a box that kept focus: tell it again (OnEngineStateChanged).
    private bool _recordingShortcut;

    /// <summary>The engine's own shortcut problems ("Alt+Z: Another app uses Alt+Z…"), for the conflict notes.</summary>
    public async Task RefreshShortcutProblemsAsync()
    {
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Status }).ConfigureAwait(false);
        _post(() =>
        {
            _shortcutProblems = answer?.Status?.ShortcutProblems ?? [];
            if (Mode == InspectorMode.Flow)
                Load();
        });
    }

    // ---- Failure-policy preview ----

    /// <summary>Hover/focus on a policy: fades what wouldn't run if this node failed; null clears it.</summary>
    public void PreviewPolicy(int? index)
    {
        if (index is not ({ } i and >= 0 and <= 2) || NodeId is null || Flow is not { } flow)
        {
            _canvas.Preview(null);
            return;
        }
        _canvas.Preview(Affected(flow, NodeId, (FailurePolicy)i));
    }

    /// <summary>
    /// What wouldn't run if <paramref name="id"/> failed (as <c>Scheduler</c> decides): Stop — everything except it and
    /// what it waits for, unless an "if it fails" edge handles it (then as Skip); Skip — what waits for it through "if it
    /// works" edges, but not past "whatever happens" edges; Run the next steps anyway — nothing. A card member fails into its card, whose
    /// policy decides. Cards bring their members.
    /// </summary>
    public static IReadOnlySet<string> Affected(Flow flow, string id, FailurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var top = id;
        if (cardOf.TryGetValue(id, out var card))
        {
            if (policy == FailurePolicy.Ignore)
                return result;
            top = card;
            policy = flow.FindNode(card)?.OnFailure ?? FailurePolicy.Stop;
        }
        var edges = FlowLayout.Edges(flow, cardOf);
        if (policy == FailurePolicy.Stop && edges.Any(e => e.From == top && e.When == EdgeWhen.Failure))
            policy = FailurePolicy.Skip;
        switch (policy)
        {
            case FailurePolicy.Stop:
                var upstream = Reach(edges, top, backwards: true, onlySuccess: false);
                upstream.Add(top);
                foreach (var node in flow.Nodes)
                {
                    if (!upstream.Contains(cardOf.GetValueOrDefault(node.Id, node.Id)))
                        result.Add(node.Id);
                }
                break;
            case FailurePolicy.Skip:
                var skipped = Reach(edges, top, backwards: false, onlySuccess: true);
                foreach (var node in flow.Nodes)
                {
                    if (skipped.Contains(cardOf.GetValueOrDefault(node.Id, node.Id)))
                        result.Add(node.Id);
                }
                break;
        }
        result.Remove(id);
        result.Remove(top);
        return result;
    }

    private static HashSet<string> Reach(IReadOnlyList<(string From, string To, EdgeWhen When)> edges, string start, bool backwards, bool onlySuccess)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([start]);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            foreach (var (from, to, when) in edges)
            {
                // Skip: the failed step fires only its "if it fails" edges; a skipped one still fires "whatever happens".
                if (onlySuccess && (at == start ? when != EdgeWhen.Success : when == EdgeWhen.Always))
                    continue;
                var (here, next) = backwards ? (to, from) : (from, to);
                if (here == at && seen.Add(next))
                    stack.Push(next);
            }
        }
        return seen;
    }

    // ---- Test this step (UX §5.4) ----

    /// <summary>Runs the selected step alone in the engine with the values on screen (saved or not).</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (Node is not StepNode step || Flow is not { } flow || !EngineRunning || IsTesting)
            return;
        // A menu resolves its flows from the config, so it gets them all (edited flow in place); other steps run alone.
        var flows = step.Step == ProviderCatalog.MenuShowId
            ? _session.Current.Flows.Select(f => f.Id == flow.Id ? flow : f).ToArray()
            : [flow];
        var yaml = ConfigYaml.Save(new Configuration { Library = _session.Current.Library, Flows = [.. flows] });
        var nodeId = step.Id;
        using var cancel = new CancellationTokenSource();
        _test = cancel;
        IsTesting = true;
        TestResult = null;
        TestDetails = null;
        PipeMessage? answer;
        try
        {
            answer = await _engine.SendAsync(new PipeRequest { Op = Ops.TestStep, Flow = flow.Id, Node = nodeId, Config = yaml }, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            answer = null;
        }
        _test = null;
        IsTesting = false;
        if (NodeId != nodeId)
            return; // the selection moved on; the result belongs to another step
        if (cancel.IsCancellationRequested)
        {
            ShowResult(false, "Test stopped.", null);
            return;
        }
        if (answer is null)
            ShowResult(false, "Didn't work: the engine isn't running.", null);
        else if (!answer.Ok)
            ShowResult(false, "Didn't work: " + answer.Error, null);
        else if (answer.Run?.Nodes.FirstOrDefault(n => n.NodeId == nodeId) is { } result)
            ShowResult(result.Outcome == NodeOutcome.Succeeded || Outcomes.IsCheck(step.Step) && result.Outcome == NodeOutcome.Failed,
                step.Step == ProviderCatalog.MenuShowId ? ExplainMenu(result, result.Summary) : ExplainTest(step.Step, result),
                Outcomes.IsCheck(step.Step) ? null : result.Error);
    }

    [RelayCommand]
    private void CancelTest() => _test?.Cancel();

    /// <summary>"Worked in 1.2 s." / "Didn't work: Windows couldn't find Technics."</summary>
    public static string Explain(NodeResult result) => result.Outcome switch
    {
        NodeOutcome.Succeeded => result.Duration < TimeSpan.FromSeconds(0.05) ? "Worked right away." : $"Worked in {Seconds(result.Duration)}.",
        NodeOutcome.Cancelled => "Test stopped.",
        _ => "Didn't work: " + FirstLine(result.Error ?? "it failed."),
    };

    private void ShowResult(bool succeeded, string text, string? details)
    {
        TestSucceeded = succeeded;
        TestResult = text;
        TestDetails = details is not null && details != FirstLine(details) ? details : null;
    }

    private void ClearTest()
    {
        _test?.Cancel();
        TestResult = null;
        TestDetails = null;
    }

    // ---- Helpers ----

    private static string FirstLine(string text) => text.ReplaceLineEndings("\n").Split('\n')[0].Trim();

    /// <summary>"1.2 s", "10 s".</summary>
    internal static string Seconds(TimeSpan time) =>
        string.Create(CultureInfo.CurrentCulture, $"{Math.Round(time.TotalSeconds, time.TotalSeconds < 10 ? 1 : 0)} s");

    private static string FirstGlyph(Flow flow) => Core.Model.FlowGlyph.FirstStepCode(flow);

    private static List<GlyphOption> BuildGlyphs()
    {
        (string Code, string Name)[] extra =
        [
            ("E8FD", "List"), ("E7F8", "Laptop"), ("E7FC", "Game"), ("E8D6", "Music"), ("E714", "Video"),
            ("E80F", "Home"), ("E706", "Brightness"), ("E708", "Moon"), ("E7E8", "Power"), ("E734", "Star"),
        ];
        return [.. ProviderCatalog.All.GroupBy(d => d.Glyph).Select(g => (Code: g.Key, Name: CanvasNodeViewModel.Area(g.First().Area)))
            .Concat(extra).DistinctBy(g => g.Code).Select(g => new GlyphOption(g.Code, FlowItemViewModel.DecodeGlyph(g.Code), g.Name))];
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfigSession.Current) or nameof(ConfigSession.Problems))
            Load();
        else if (e.PropertyName == nameof(ConfigSession.CanEdit))
            OnPropertyChanged(nameof(CanEdit));
    }

    private void OnCanvasChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlowCanvasViewModel.SelectedIds))
        {
            ManyProblem = ""; // a refusal belongs to the selection it was for
            Load();
        }
        else if (e.PropertyName == nameof(FlowCanvasViewModel.CanEdit))
            OnPropertyChanged(nameof(CanEdit));
    }

    private void OnEngineStateChanged() => _post(() =>
    {
        OnPropertyChanged(nameof(EngineRunning));
        OnPropertyChanged(nameof(CanTest));
        Load(); // "Capture current layout" comes and goes with the engine
        _ = RefreshDesktopAsync();
        if (EngineRunning)
        {
            if (_recordingShortcut)
                _ = _engine.SendAsync(new PipeRequest { Op = Ops.RecordHotkey, On = true });
            _ = RefreshShortcutProblemsAsync();
            foreach (var kind in Fields.Select(f => f.Spec.Library).OfType<LibraryKind>().Distinct())
                _ = RefreshDevicesAsync(kind);
            // The screens checklist has no drop-down to retry from.
            foreach (var kind in Fields.Select(f => f.Spec.Suggest).Where(s => s?.Source == SuggestSource.Devices && s.Kind != ParamSuggest.AndroidApps).Select(s => s!.Kind).Distinct())
                _ = RefreshSuggestionsAsync(kind);
        }
    });

    /// <summary>The engine registers shortcuts after each reload and retries failed ones (D-072): conflicts change then.</summary>
    private void OnEngineEvent(PipeEvent e)
    {
        if (e.Kind == EventKinds.ShortcutsChanged)
            _ = RefreshShortcutProblemsAsync();
        if (e.Kind == EventKinds.ConfigReloaded)
        {
            _ = RefreshShortcutProblemsAsync();
            _ = RefreshDesktopAsync(); // a saved rename makes the shortcut's name old
        }
    }

    public void Dispose()
    {
        _test?.Cancel();
        _canvas.Preview(null);
        _session.PropertyChanged -= OnSessionChanged;
        _canvas.PropertyChanged -= OnCanvasChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.EventReceived -= OnEngineEvent;
        _desktop.Changed -= OnDesktopChanged;
        _defaults?.Dispose();
        ClearFields();
    }
}
