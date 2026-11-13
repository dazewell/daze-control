using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Editor.ViewModels.Inspector;

/// <summary>A device picker's rows and selection, shared by the flow's input rows and a call's input pickers.</summary>
public abstract partial class DevicePickerViewModel : ObservableObject
{
    private protected bool Loading;

    public ObservableCollection<DeviceOption> Devices { get; } = [];

    [ObservableProperty]
    public partial DeviceOption? Device { get; set; }

    /// <summary>The validator's problem for this row, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(AutomationName))]
    public partial string? Error { get; internal set; }

    public bool HasError => Error is not null;

    public abstract string Title { get; }

    /// <summary>The Library kind the rows are of (the pane refreshes this PC's devices when the drop-down opens).</summary>
    public abstract LibraryKind Kind { get; }

    public string AutomationName => Error is null ? Title : $"{Title}. {Error}";

    /// <summary>Replaces the rows (only when one really changed: rebuilding closes an open drop-down) and selects <paramref name="current"/>.</summary>
    internal void SetDevices(IReadOnlyList<DeviceOption> options, string? current)
    {
        Loading = true;
        try
        {
            static (DeviceSource, string?, string, string, string, bool) Row(DeviceOption o) => (o.Source, o.Key, o.Title, o.Caption, o.Glyph, o.IsEnabled);
            if (!Devices.Select(Row).SequenceEqual(options.Select(Row)))
            {
                Devices.Clear();
                foreach (var option in options)
                    Devices.Add(option);
            }
            Device = Devices.FirstOrDefault(o => o.Key == current && o.IsValue) ?? Devices.FirstOrDefault(o => o.Source == DeviceSource.None);
            OnPropertyChanged(nameof(Device)); // the drop-down drops its selection when its rows are replaced
        }
        finally
        {
            Loading = false;
        }
    }

    partial void OnDeviceChanged(DeviceOption? oldValue, DeviceOption? newValue)
    {
        if (Loading || newValue is null)
            return;
        if (!newValue.IsEnabled)
        {
            Loading = true;
            Device = oldValue; // a heading or a greyed row, reached by keyboard
            Loading = false;
            return;
        }
        Picked(newValue);
    }

    private protected abstract void Picked(DeviceOption option);
}

/// <summary>
/// One of the flow's inputs in its settings (UX §5.4, D-064): name, kind and default device. The input's key
/// (<c>$output</c>) is made once from its kind and never changes, so renaming it can't break a step.
/// </summary>
public sealed partial class FlowInputViewModel : DevicePickerViewModel
{
    /// <summary>The kind ComboBox's rows, in <see cref="Kinds"/> order, then Text (T23.7).</summary>
    public static IReadOnlyList<string> KindNames { get; } = ["Audio device", "Bluetooth device", "Bound device", "Android device", "Screen layout", "Text"];

    /// <summary>The kind row of a text input: after the devices.</summary>
    internal static int TextIndex => Kinds.Length;

    internal static readonly LibraryKind[] Kinds = [LibraryKind.Audio, LibraryKind.Bluetooth, LibraryKind.Bound, LibraryKind.Android, LibraryKind.Display];

    private readonly InspectorViewModel _owner;

    internal FlowInputViewModel(InspectorViewModel owner, string name)
    {
        _owner = owner;
        Name = name;
    }

    /// <summary>The key steps use as <c>$name</c>.</summary>
    public string Name { get; }

    public override string Title => Label.Length == 0 ? "Input" : Label;

    /// <summary>"Output": what the editor and messages call it; commits on leaving the box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(AutomationName), nameof(RemoveName), nameof(DefaultName), nameof(AskName))]
    public partial string Label { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsDevice))]
    public partial int KindIndex { get; set; }

    public override LibraryKind Kind => Kinds[Math.Clamp(KindIndex, 0, Kinds.Length - 1)];

    /// <summary>A text input (T23.7): a default text box instead of the device picker, and no "Ask me".</summary>
    public bool IsText => KindIndex == TextIndex;

    public bool IsDevice => !IsText;

    /// <summary>A text input's default: the text steps get as <c>{$name}</c> when the caller passes none. Commits on leaving the box.</summary>
    [ObservableProperty]
    public partial string TextDefault { get; set; } = "";

    public string DefaultTextName => $"{Title}: default text";

    /// <summary>"Ask me when it runs" (D-107): a prompt offers the Library items of the kind before each run.</summary>
    [ObservableProperty]
    public partial bool Ask { get; set; }

    /// <summary>Seconds to wait for an answer (5 s to 10 min).</summary>
    [ObservableProperty]
    public partial double TimeoutSeconds { get; set; } = Prompts.DefaultTimeout.TotalSeconds;

    /// <summary>"No answer" rows: 0 = by how it started (empty), 1 = stop the flow, 2 = use the default.</summary>
    [ObservableProperty]
    public partial int OnTimeoutIndex { get; set; }

    public static IReadOnlyList<string> OnTimeoutNames { get; } = ["As usual", "Stop the flow", "Use the default"];

    internal static readonly string[] OnTimeoutValues = ["", Prompts.Stop, Prompts.UseDefault];

    public string AskName => $"{Title}: ask me when it runs";

    public string RemoveName => $"Remove {Title}";

    public string DefaultName => $"{Title}: default device";

    /// <summary>A bound device is required, not a system default (F-008).</summary>
    public string DefaultPlaceholder => Kind == LibraryKind.Bound ? "Choose a bound device" : "Default device";

    public const string NoBoundDevicesHint = "No bound devices yet.";

    /// <summary>The Library has no bound devices: a line with a link to bind some.</summary>
    [ObservableProperty]
    public partial bool NoBoundDevices { get; private set; }

    internal void SetNoBoundDevices(bool value) => NoBoundDevices = value;

    public void OpenBoundDevices() => _owner.RequestBoundDevices();

    internal void Load(FlowInput input)
    {
        Loading = true;
        try
        {
            Label = input.Label;
            KindIndex = input.Kind is { } kind ? Math.Max(0, Array.IndexOf(Kinds, kind)) : TextIndex;
            TextDefault = input.IsText ? input.Default : "";
            OnPropertyChanged(nameof(DefaultPlaceholder));
            Ask = input.Ask;
            TimeoutSeconds = (input.Timeout ?? Prompts.DefaultTimeout).TotalSeconds;
            OnTimeoutIndex = Math.Max(0, Array.IndexOf(OnTimeoutValues, input.OnTimeout));
        }
        finally
        {
            Loading = false;
        }
    }

    partial void OnLabelChanged(string value)
    {
        if (!Loading)
            _owner.SetInputLabel(Name, value.Trim());
    }

    partial void OnAskChanged(bool value)
    {
        if (!Loading)
            _owner.SetInputAsk(Name, value);
    }

    partial void OnTimeoutSecondsChanged(double value)
    {
        if (!Loading)
            _owner.SetInputTimeout(Name, double.IsNaN(value) ? Prompts.DefaultTimeout : TimeSpan.FromSeconds(value));
    }

    partial void OnOnTimeoutIndexChanged(int value)
    {
        if (!Loading && value >= 0 && value < OnTimeoutValues.Length)
            _owner.SetInputOnTimeout(Name, OnTimeoutValues[value]);
    }

    partial void OnKindIndexChanged(int value)
    {
        if (!Loading && value >= 0 && value <= TextIndex)
            _owner.SetInputKind(Name, value == TextIndex ? null : Kinds[value]);
    }

    partial void OnTextDefaultChanged(string value)
    {
        if (!Loading && IsText)
            _owner.SetInputText(Name, value);
    }

    private protected override void Picked(DeviceOption option) => _owner.SetInputDefault(Name, Kind, option);

    public void Remove() => _owner.RemoveInput(Name);
}

/// <summary>
/// The device a <c>flow.run</c> step passes to one input of the flow it runs (UX §5.4): "Its default: Technics", a
/// Library device of the input's kind, or one of this flow's own inputs of that kind (pass-through, <c>$name</c>).
/// </summary>
public sealed partial class CallInputViewModel : DevicePickerViewModel
{
    private readonly Action<CallInputViewModel, DeviceOption> _picked;

    private readonly Action<CallInputViewModel, string>? _typed;

    internal CallInputViewModel(string name, FlowInput input, Action<CallInputViewModel, DeviceOption> picked,
        Action<CallInputViewModel, string>? typed = null)
    {
        Name = name;
        Input = input;
        _picked = picked;
        _typed = typed;
    }

    public string Name { get; }

    internal FlowInput Input { get; }

    public override string Title => Input.Label;

    /// <summary>A text input's rows are never shown; the kind only matters for device inputs.</summary>
    public override LibraryKind Kind => Input.Kind ?? LibraryKind.Audio;

    /// <summary>A text input (T23.7): a text box that may hold values of this flow's earlier steps, filled when the call runs.</summary>
    public bool IsText => Input.IsText;

    public bool IsDevice => !Input.IsText;

    /// <summary>What this call passes to a text input; empty = the called flow's default. Commits on leaving the box.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>"Its default: Hi" under an empty box.</summary>
    public string TextPlaceholder => Input.Default.Length == 0 ? "Its default: nothing" : "Its default: " + Input.Default;

    internal void LoadText(string text)
    {
        Loading = true;
        try
        {
            Text = text;
        }
        finally
        {
            Loading = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (!Loading)
            _typed?.Invoke(this, value);
    }

    private protected override void Picked(DeviceOption option) => _picked(this, option);
}
