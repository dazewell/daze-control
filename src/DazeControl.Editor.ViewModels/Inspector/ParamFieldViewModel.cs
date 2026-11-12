using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>
/// One setting of the selected step, rendered from its <see cref="ParamSpec"/> (UX §5.4). Each control's value is
/// written back as the step's <c>with:</c> entry the moment it changes (text boxes commit on leaving them); a value
/// equal to the default is left out of the YAML.
/// </summary>
public sealed partial class ParamFieldViewModel : ObservableObject
{
    private readonly Action<ParamFieldViewModel, ParamValue?> _write;
    private readonly Func<string, bool> _fileExists;
    private bool _loading;

    internal ParamFieldViewModel(ParamSpec spec, Action<ParamFieldViewModel, ParamValue?> write, Func<string, bool> fileExists)
    {
        Spec = spec;
        _write = write;
        _fileExists = fileExists;
    }

    public ParamSpec Spec { get; }

    public string Name => Spec.Name;

    public ParamKind Kind => Spec.Kind;

    public string Title => Spec.Title;

    public string Help => Spec.Help ?? "";

    public bool HasHelp => Help.Length > 0;

    /// <summary>The default, shown where nothing is written.</summary>
    public string Placeholder => IsMixed ? "Mixed" : Kind switch
    {
        ParamKind.TextList => "One per line",
        ParamKind.TextMap => "Name: value, one per line",
        ParamKind.Choice when Spec.Default is { } d => Spec.CaptionOf(d),
        _ => Spec.Default ?? (Spec.Required ? "" : "Optional"),
    };

    public bool IsText => Kind is ParamKind.Text && Spec.Suggest is null && !IsKindDevice;

    /// <summary>A Library key of the kind another setting chooses (Pick's <c>default</c>, D-128): a device picker, stored as the key.</summary>
    public bool IsKindDevice => Kind is ParamKind.Text && Spec.LibraryFrom is not null;

    /// <summary>The key a stored value stands for (a hand-typed name resolves to its item); set by the inspector. Only for display: nothing is rewritten.</summary>
    internal Func<string?, string?>? ShownKey { get; set; }

    /// <summary>Text with suggestions: an editable ComboBox, typing stays allowed (D-050).</summary>
    public bool IsSuggestText => Kind is ParamKind.Text && Spec.Suggest is not null && !IsFlowRef && !IsAppPicker;

    /// <summary>An Android app (package id): a search box over the device's apps by name, typing a package allowed (D-094).</summary>
    public bool IsAppPicker => Kind is ParamKind.Text && Spec.Suggest is { Source: SuggestSource.Devices, Kind: ParamSuggest.AndroidApps };

    /// <summary>Another flow (<c>flow.run</c>): a picker of names that stores the id, like a device picker.</summary>
    public bool IsFlowRef => Kind is ParamKind.Text && Spec.RefersTo == ConfigRef.Flow;

    /// <summary>An unordered list picked from the PC (screens): a checklist (D-051).</summary>
    public bool IsChecklist => Kind is ParamKind.TextList && Spec.Suggest is not null && !Spec.Ordered;

    /// <summary>An ordered list of flows (<c>menu.show</c>): glyph + name tokens and an "Add a flow" box (DESIGN §3.7).</summary>
    public bool IsFlowList => Kind is ParamKind.TextList && Spec.Ordered && Spec.RefersTo == ConfigRef.Flow;

    /// <summary>An ordered list picked from a table (Android keys): tokens in send order plus an add box.</summary>
    public bool IsTokenList => Kind is ParamKind.TextList && Spec.Suggest is not null && Spec.Ordered && !IsFlowList;

    public bool IsMultiline => !IsCallInputs && (Kind is ParamKind.MultilineText or ParamKind.TextMap || Kind is ParamKind.TextList && Spec.Suggest is null);

    public bool HasExamples => !IsCallInputs && Spec.Examples.Count > 0;

    /// <summary><c>flow.run</c>'s <c>inputs</c>: one device picker per input of the flow it runs (UX §5.4), not a text map.</summary>
    public bool IsCallInputs { get; init; }

    public ObservableCollection<CallInputViewModel> CallInputs { get; } = [];

    /// <summary>"Choose a flow first." / "Night takes no inputs."; "" while there are pickers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCallInputsHint))]
    public partial string CallInputsHint { get; internal set; } = "";

    public bool HasCallInputsHint => CallInputsHint.Length > 0;

    /// <summary>Keeps the rows while the called flow's inputs stay the same (an open drop-down stays open).</summary>
    /// <returns>True when the rows were rebuilt.</returns>
    internal bool SetCallInputs(IReadOnlyList<KeyValuePair<string, FlowInput>> inputs, Func<string, FlowInput, CallInputViewModel> create)
    {
        if (CallInputs.Select(c => (c.Name, c.Input)).SequenceEqual(inputs.Select(i => (i.Key, i.Value))))
            return false;
        CallInputs.Clear();
        foreach (var (name, input) in inputs)
            CallInputs.Add(create(name, input));
        return true;
    }

    /// <summary>A problem the field itself can't see (an input passed that the called flow doesn't have).</summary>
    internal void SetError(string? problem)
    {
        Error = problem;
        OnPropertyChanged(nameof(AutomationName));
    }

    public IReadOnlyList<Core.Catalog.ParamExample> Examples => Spec.Examples;

    /// <summary>Editable ComboBox rows (services, saved sign-ins, flags, the device's inputs).</summary>
    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];

    /// <summary>Shown when there's nothing to suggest yet: "Add inputs in the Library".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestionHint), nameof(HasHintLink))]
    public partial string SuggestionHint { get; private set; } = "";

    public bool HasSuggestionHint => SuggestionHint.Length > 0 && HintDevice is null;

    /// <summary>
    /// Android input: the device whose inputs the hint's link opens in the Library (L1), or null for a plain hint.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestionHint), nameof(HasHintLink))]
    public partial string? HintDevice { get; private set; }

    /// <summary>"Add inputs in the Library" as a link to that device's inputs.</summary>
    public bool HasHintLink => SuggestionHint.Length > 0 && HintDevice is not null;

    public ObservableCollection<ScreenCheckViewModel> Screens { get; } = [];

    public ObservableCollection<KeyTokenViewModel> Tokens { get; } = [];

    public ObservableCollection<FlowTokenViewModel> FlowTokens { get; } = [];

    /// <summary>The "Add a flow" box's rows: the other flows not in the menu yet.</summary>
    public ObservableCollection<FlowChoice> FlowChoices { get; } = [];

    public bool CanAddFlow => _items.Count < ProviderCatalog.MaxMenuItems;

    private (Configuration Config, IReadOnlyList<Problem> Problems, string SelfId)? _flowContext;

    /// <summary>The open config, so the tokens can show names, glyphs and captions.</summary>
    internal void SetFlowContext(Configuration config, IReadOnlyList<Problem> problems, string selfId)
    {
        _flowContext = (config, problems, selfId);
        RebuildFlowTokens();
    }

    private void RebuildFlowTokens()
    {
        if (!IsFlowList || _flowContext is not var (config, problems, selfId))
            return;
        var rows = Inspector.FlowTokens.Rows(config, problems, selfId, _items).Select(r => r with { Owner = this }).ToList();
        if (!FlowTokens.SequenceEqual(rows))
        {
            FlowTokens.Clear();
            foreach (var row in rows)
                FlowTokens.Add(row);
        }
        var choices = Inspector.FlowTokens.Choices(config, problems, selfId, _items);
        if (!FlowChoices.SequenceEqual(choices))
        {
            FlowChoices.Clear();
            foreach (var choice in choices)
                FlowChoices.Add(choice);
        }
        OnPropertyChanged(nameof(CanAddFlow));
    }

    /// <summary>Adds a flow (by id) at the end of the menu; ignored when it is listed or the menu is full.</summary>
    public void AddFlow(string id)
    {
        id = id.Trim();
        if (id.Length > 0 && CanAddFlow && !_items.Contains(id))
            WriteItems([.. _items, id]);
    }

    public void RemoveFlow(FlowTokenViewModel token) => RemoveAt(token.Index);

    public void MoveFlow(FlowTokenViewModel token, int by) => MoveAt(token.Index, by);

    /// <summary>The list as the step has it (checklist and tokens).</summary>
    private List<string> _items = [];

    private IReadOnlyList<Ipc.DeviceInfo>? _liveScreens;

    /// <summary>Scripts and bodies: monospace, 8 lines (UX §5.4).</summary>
    public bool IsCode => Kind is ParamKind.MultilineText;

    public bool IsFile => Kind is ParamKind.FilePath or ParamKind.FolderPath;

    /// <summary>Browse picks a folder, not a file.</summary>
    public bool IsFolder => Kind is ParamKind.FolderPath;

    public bool IsNumber => Kind is ParamKind.WholeNumber or ParamKind.Duration;

    /// <summary>NumberBox suffix: durations are edited in seconds.</summary>
    public string Unit => Kind == ParamKind.Duration ? "s" : "";

    public double Minimum => Spec.Min ?? 0;

    public double Maximum => Spec.Max ?? (Kind == ParamKind.Duration ? 86400 : int.MaxValue);

    public bool IsBoolean => Kind is ParamKind.Boolean;

    public bool IsChoice => Kind is ParamKind.Choice;

    /// <summary>A picker of rows with a glyph and caption: Library items, or other flows.</summary>
    public bool IsDevice => Kind is ParamKind.LibraryRef || IsFlowRef || IsKindDevice;

    public IReadOnlyList<string> Choices => Spec.Choices;

    /// <summary>The drop-down rows: value (as in YAML) and its words. Built once so the control's selection can be matched by reference.</summary>
    public IReadOnlyList<ChoiceOption> ChoiceOptions => _choiceOptions ??= [.. Spec.Choices.Select(c => new ChoiceOption(c, Spec.CaptionOf(c)))];

    private IReadOnlyList<ChoiceOption>? _choiceOptions;

    /// <summary>The row of the current <see cref="Choice"/>; null while mixed or unset.</summary>
    public ChoiceOption? ChoiceOption
    {
        get => Choice is null ? null : ChoiceOptions.FirstOrDefault(o => o.Value == Choice);
        set
        {
            if (value is not null)
                Choice = value.Value;
        }
    }

    /// <summary>Text, script, file path, list (one per line) or map ("Name: value" lines).</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Whole number, or a duration in seconds; NaN = not set.</summary>
    [ObservableProperty]
    public partial double Number { get; set; } = double.NaN;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial string? Choice { get; set; }

    public ObservableCollection<DeviceOption> Devices { get; } = [];

    [ObservableProperty]
    public partial DeviceOption? Device { get; set; }

    /// <summary>The step's problem for this setting, or "Can't find this file."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The control's accessible name: title, plus the problem.</summary>
    public string AutomationName => (Error is null ? Title : $"{Title}. {Error}") + (IsMixed ? ", mixed" : "");

    /// <summary>Many mode (T15.3): the selected steps hold different values; the control shows "Mixed" and nothing is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder), nameof(AutomationName), nameof(MixedText))]
    public partial bool IsMixed { get; private set; }

    /// <summary>"Mixed" while the steps differ, else empty (a ComboBox placeholder and a caption).</summary>
    public string MixedText => IsMixed ? "Mixed" : "";

    public bool HasMixedCaption => IsMixed && Kind == ParamKind.Boolean;

    /// <summary>A device row was picked that isn't a Library key yet (from Windows, capture): the inspector handles it.</summary>
    internal event Action<ParamFieldViewModel, DeviceOption>? DevicePicked;

    /// <summary>Shows the step's value and problem without writing anything back.</summary>
    internal void Load(ParamValue? value, string? problem, bool mixed = false)
    {
        _loading = true;
        try
        {
            IsMixed = mixed;
            OnPropertyChanged(nameof(HasMixedCaption));
            var scalar = (value as ScalarParam)?.Text;
            switch (Kind)
            {
                case ParamKind.WholeNumber:
                    Number = double.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
                    break;
                case ParamKind.Duration:
                    Number = Duration.TryParse(scalar, out var d) ? d.TotalSeconds : double.NaN;
                    break;
                case ParamKind.Boolean:
                    IsOn = bool.TryParse(scalar ?? Spec.Default, out var b) && b;
                    break;
                case ParamKind.Choice:
                    Choice = scalar ?? Spec.Default;
                    break;
                case ParamKind.LibraryRef:
                case ParamKind.Text when IsFlowRef || IsKindDevice:
                    Text = scalar ?? "";
                    var shown = ShownKey?.Invoke(scalar) ?? scalar?.Trim();
                    Device = Devices.FirstOrDefault(o => o.Key == shown && o.IsValue)
                        ?? Devices.FirstOrDefault(o => o.Source == DeviceSource.None);
                    break;
                case ParamKind.TextList:
                    _items = value switch
                    {
                        ListParam list => [.. list.Items.OfType<ScalarParam>().Select(i => i.Text)],
                        _ => scalar is { Length: > 0 } one ? [one] : [],
                    };
                    Text = string.Join(Environment.NewLine, _items);
                    RebuildScreens();
                    RebuildTokens();
                    RebuildFlowTokens();
                    break;
                case ParamKind.TextMap:
                    Text = value is MapParam map ? string.Join(Environment.NewLine, map.Entries.Select(e => $"{e.Key}: {e.Value}")) : "";
                    break;
                default:
                    Text = _values is { } ctx && StepValues.Takes(Spec) ? ValueText.ToDisplay(ctx.Flow, scalar, _catalog) : scalar ?? "";
                    break;
            }
            if (mixed && Kind == ParamKind.Choice)
                Choice = null;
            if (mixed && IsDevice)
                Device = null;
            // Same value again raises nothing, but the control may have dropped its selection since: read both again.
            if (IsChoice)
            {
                OnPropertyChanged(nameof(Choice));
                OnPropertyChanged(nameof(ChoiceOption));
            }
            if (IsDevice)
                OnPropertyChanged(nameof(Device));
            Error = problem ?? FileProblem();
            OnPropertyChanged(nameof(AutomationName));
        }
        finally
        {
            _loading = false;
        }
        RefreshPills();
    }

    /// <summary>Replaces the picker's rows, keeping the selection on the same key.</summary>
    internal void SetDevices(IReadOnlyList<DeviceOption> options, string? current)
    {
        _loading = true;
        try
        {
            // Rebuilding the list closes an open drop-down: only when a row really changed (DeviceInfo compares by reference).
            static (DeviceSource, string?, string, string, string) Row(DeviceOption o) => (o.Source, o.Key, o.Title, o.Caption, o.Glyph);
            if (!Devices.Select(Row).SequenceEqual(options.Select(Row)))
            {
                Devices.Clear();
                foreach (var option in options)
                    Devices.Add(option);
            }
            var shown = ShownKey?.Invoke(current) ?? current?.Trim();
            Device = Devices.FirstOrDefault(o => o.Key == shown && o.IsValue)
                ?? Devices.FirstOrDefault(o => o.Source == DeviceSource.None);
            if (IsMixed)
                Device = null;
            // The drop-down drops its selection when its rows are replaced and pushes null back; read the value again.
            OnPropertyChanged(nameof(Device));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Fills the box with an example; the write is one undo step (D-050).</summary>
    public void ApplyExample(Core.Catalog.ParamExample example) => Text = example.Value;

    internal void SetSuggestions(IEnumerable<SuggestionItem> items, string hint, string? hintDevice = null)
    {
        HintDevice = hintDevice;
        var rows = items.ToList();
        if (!Suggestions.SequenceEqual(rows))
        {
            Suggestions.Clear();
            foreach (var row in rows)
                Suggestions.Add(row);
        }
        SuggestionHint = rows.Count == 0 ? hint : "";
    }

    internal void SetSuggestionHint(string hint) => SuggestionHint = hint;

    private IReadOnlyList<AppItem> _apps = [];

    /// <summary>The Android device whose apps the picker lists; null until one is chosen.</summary>
    internal AndroidDevice? AppsDevice { get; private set; }

    /// <summary>The device was asked once for a fresh list since this step was selected (D-093).</summary>
    internal bool AppsRefreshed { get; set; }

    /// <summary>Rows for what is typed in the app box, by name (D-094).</summary>
    public IReadOnlyList<AppItem> AppMatches(string query) => AppChoices.Match(_apps, query);

    /// <summary>The name of the app whose package is stored; empty when unknown (shown under the box).</summary>
    public string AppCaption => _apps.FirstOrDefault(a => a.Package == Text.Trim())?.Name ?? "";

    public bool HasAppCaption => AppCaption.Length > 0;

    /// <summary>The picker's drop-down rows: the apps matching <see cref="SetAppQuery"/>, by name.</summary>
    public ObservableCollection<AppItem> AppRows { get; } = [];

    private string _appQuery = "";

    public void SetAppQuery(string query)
    {
        _appQuery = query;
        var rows = AppMatches(query);
        if (!AppRows.SequenceEqual(rows))
        {
            AppRows.Clear();
            foreach (var row in rows)
                AppRows.Add(row);
        }
    }

    internal void SetApps(AndroidDevice? device, IEnumerable<AppItem> apps, string hint)
    {
        if (AppsDevice?.Serial != device?.Serial)
            AppsRefreshed = false;
        AppsDevice = device;
        _apps = AppChoices.Sorted(apps);
        HintDevice = null;
        SuggestionHint = hint;
        OnPropertyChanged(nameof(AppCaption));
        OnPropertyChanged(nameof(HasAppCaption));
        SetAppQuery(_appQuery);
    }

    /// <summary>The engine's screens (null: not known); rebuilds the checklist around the step's entries.</summary>
    internal void SetScreens(IReadOnlyList<Ipc.DeviceInfo>? live)
    {
        _liveScreens = live;
        RebuildScreens();
    }

    /// <summary>#27: checks exactly the screens that are on now.</summary>
    public void UseScreensOnNow()
    {
        if (!IsChecklist || _liveScreens is null)
            return;
        foreach (var row in Screens)
            row.Load(row.Plugged && row.On);
        WriteItems(ScreenChoices.Entries(Screens));
    }

    /// <summary>Android keys: matches for the add box, the table's order.</summary>
    public IReadOnlyList<string> KeySuggestions(string query) =>
        [.. (Spec.Suggest?.Values ?? []).Where(k => k.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))];

    public void AddToken(string text)
    {
        var key = text.Trim();
        if (key.Length > 0)
            WriteItems([.. _items, key]);
    }

    public void RemoveToken(KeyTokenViewModel token) => RemoveAt(token.Index);

    private void RemoveAt(int index)
    {
        if (index >= 0 && index < _items.Count)
            WriteItems([.. _items.Where((_, i) => i != index)]);
    }

    /// <summary>Alt+Up/Down: moves a key one place in the send order.</summary>
    public void MoveToken(KeyTokenViewModel token, int by) => MoveAt(token.Index, by);

    private void MoveAt(int index, int by)
    {
        var to = index + by;
        if (index < 0 || index >= _items.Count || to < 0 || to >= _items.Count)
            return;
        var items = _items.ToList();
        (items[index], items[to]) = (items[to], items[index]);
        WriteItems(items);
    }

    private void WriteItems(List<string> items)
    {
        _items = items;
        RebuildTokens();
        RebuildFlowTokens();
        if (!IsChecklist)
            RebuildScreens();
        _write(this, items.Count > 0 ? new ListParam([.. items.Select(i => (ParamValue)i)]) : null);
    }

    private void RebuildScreens()
    {
        if (!IsChecklist)
            return;
        var rows = ScreenChoices.Rows(_liveScreens, _items);
        if (rows.Count == Screens.Count && rows.Zip(Screens).All(p => p.First.Title == p.Second.Title
                && p.First.Caption == p.Second.Caption && p.First.Value == p.Second.Value))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                Screens[i].Entry = rows[i].Entry;
                Screens[i].Load(rows[i].IsChecked);
            }
            return;
        }
        Screens.Clear();
        foreach (var row in rows)
        {
            row.Toggled = () => WriteItems(ScreenChoices.Entries(Screens));
            Screens.Add(row);
        }
    }

    private void RebuildTokens()
    {
        if (!IsTokenList)
            return;
        Tokens.Clear();
        for (var i = 0; i < _items.Count; i++)
            Tokens.Add(new KeyTokenViewModel(_items[i], i, _items.Count) { Owner = this });
    }

    private (Flow Flow, string StepId)? _values;
    private IReadOnlyList<ProviderDescriptor>? _catalog;

    /// <summary>Whether the setting takes values here: it is marked so and one step is selected (several steps depend on their own upstream, DESIGN §5.7).</summary>
    public bool TakesValues => _values is not null && StepValues.Takes(Spec) && !IsMixed;

    /// <summary>"Insert a value into Message": the button's accessible name.</summary>
    public string InsertValueName => $"Insert a value into {Title}";

    /// <summary>Gives the field the flow and step its references are read against; null (several steps) turns Insert value off and shows the text as stored.</summary>
    internal void SetValueContext(Flow? flow, string? stepId, IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        _catalog = catalog;
        var next = flow is null || stepId is null ? ((Flow, string)?)null : (flow, stepId);
        var changed = _values?.Flow != next?.Item1 || _values?.StepId != next?.Item2;
        _values = next;
        if (changed)
        {
            OnPropertyChanged(nameof(TakesValues));
            OnPropertyChanged(nameof(ShowsPlainHelp));
        }
        RefreshPills();
    }

    /// <summary>The groups of the Insert value list; empty when the setting takes no values.</summary>
    public IReadOnlyList<ValueGroup> ValueGroups => _values is { } c && TakesValues ? ValueText.Choices(c.Flow, c.StepId, _catalog) : [];

    /// <summary>Puts a value at the caret (in place of a selection) and returns where the caret goes. One write, one undo step.</summary>
    public int InsertValue(ValueChoice choice, int selectionStart, int selectionLength)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Text = ValueText.Insert(Text, selectionStart, selectionLength, choice.Display, out var caret);
        return caret;
    }

    private string Stored(string shown) => _values is { } c && StepValues.Takes(Spec) ? ValueText.FromDisplay(c.Flow, shown, _catalog) : shown;

    partial void OnTextChanged(string value)
    {
        RefreshPills();
        if (IsAppPicker)
        {
            OnPropertyChanged(nameof(AppCaption));
            OnPropertyChanged(nameof(HasAppCaption));
        }
        if (_loading)
            return;
        Error = FileProblem();
        OnPropertyChanged(nameof(AutomationName));
        _write(this, Kind switch
        {
            ParamKind.TextList => Lines(value) is { Count: > 0 } lines ? new ListParam([.. lines.Select(l => (ParamValue)l)]) : null,
            ParamKind.TextMap => Lines(value).Where(l => l.Contains(':', StringComparison.Ordinal)).ToList() is { Count: > 0 } pairs
                ? new MapParam(new(pairs.Select(p => new KeyValuePair<string, ParamValue>(p[..p.IndexOf(':', StringComparison.Ordinal)].Trim(),
                    p[(p.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()))))
                : null,
            // Scripts keep their own line breaks and indentation; a one-line value is trimmed.
            ParamKind.MultilineText => Scalar(value.Trim().Length == 0 ? "" : Stored(value.ReplaceLineEndings("\n").TrimEnd())),
            _ => Scalar(Stored(value.Trim())),
        });
    }

    partial void OnNumberChanged(double value)
    {
        if (_loading)
            return;
        if (double.IsNaN(value))
        {
            _write(this, null);
            return;
        }
        var clamped = Math.Clamp(value, Minimum, Maximum);
        _write(this, Kind == ParamKind.Duration
            ? Scalar(Duration.Format(TimeSpan.FromMilliseconds(Math.Round(clamped * 1000))))
            : Scalar(((long)Math.Round(clamped)).ToString(CultureInfo.InvariantCulture)));
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading)
            _write(this, Scalar(value ? "true" : "false"));
    }

    partial void OnChoiceChanged(string? value)
    {
        OnPropertyChanged(nameof(ChoiceOption));
        if (!_loading && value is not null)
            _write(this, Scalar(value));
    }

    partial void OnDeviceChanged(DeviceOption? oldValue, DeviceOption? newValue)
    {
        if (_loading || newValue is not { } value)
            return;
        if (!value.IsEnabled)
        {
            // The drop-down greys the row out; keyboard selection can still land on it.
            _loading = true;
            Device = oldValue;
            _loading = false;
            return;
        }
        switch (value.Source)
        {
            case DeviceSource.None:
                _write(this, null);
                break;
            case DeviceSource.Library:
            case DeviceSource.Missing:
            case DeviceSource.Input:
                _write(this, Scalar(value.Key ?? ""));
                break;
            default:
                DevicePicked?.Invoke(this, value);
                break;
        }
    }

    /// <summary>Empty and the default are left out of the YAML.</summary>
    private ScalarParam? Scalar(string text) => text.Length == 0 || text == Spec.Default ? null : new ScalarParam(text);

    private static List<string> Lines(string text) =>
        [.. text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Only full paths are checked: a bare "notepad.exe" is found on PATH at run time.</summary>
    private string? FileProblem()
    {
        if (!IsFile || Text.Trim() is not { Length: > 0 } path)
            return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        return Path.IsPathFullyQualified(path) && !_fileExists(path) ? (IsFolder ? "Can't find this folder." : "Can't find this file.") : null;
    }
}

/// <summary>A drop-down row of a choice param: the stored <see cref="Value"/> and its <see cref="Caption"/> (D-102).</summary>
public sealed record ChoiceOption(string Value, string Caption)
{
    public override string ToString() => Caption;
}
