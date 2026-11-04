using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Model;

namespace DazeControl.Editor.ViewModels.Inspector;

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
    /// <summary>The kind ComboBox's rows, in <see cref="Kinds"/> order.</summary>
    public static IReadOnlyList<string> KindNames { get; } = ["Audio device", "Bluetooth device", "Android device", "Screen layout"];

    internal static readonly LibraryKind[] Kinds = [LibraryKind.Audio, LibraryKind.Bluetooth, LibraryKind.Android, LibraryKind.Display];

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
    [NotifyPropertyChangedFor(nameof(Title), nameof(AutomationName), nameof(RemoveName), nameof(DefaultName))]
    public partial string Label { get; set; } = "";

    [ObservableProperty]
    public partial int KindIndex { get; set; }

    public LibraryKind Kind => Kinds[Math.Clamp(KindIndex, 0, Kinds.Length - 1)];

    public string RemoveName => $"Remove {Title}";

    public string DefaultName => $"{Title}: default device";

    internal void Load(FlowInput input)
    {
        Loading = true;
        try
        {
            Label = input.Label;
            KindIndex = Math.Max(0, Array.IndexOf(Kinds, input.Kind));
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

    partial void OnKindIndexChanged(int value)
    {
        if (!Loading && value >= 0 && value < Kinds.Length)
            _owner.SetInputKind(Name, Kinds[value]);
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

    internal CallInputViewModel(string name, FlowInput input, Action<CallInputViewModel, DeviceOption> picked)
    {
        Name = name;
        Input = input;
        _picked = picked;
    }

    public string Name { get; }

    internal FlowInput Input { get; }

    public override string Title => Input.Label;

    private protected override void Picked(DeviceOption option) => _picked(this, option);
}
