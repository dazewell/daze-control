using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;

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
    public string Placeholder => Kind switch
    {
        ParamKind.TextList => "One per line",
        ParamKind.TextMap => "Name: value, one per line",
        _ => Spec.Default ?? (Spec.Required ? "" : "Optional"),
    };

    public bool IsText => Kind is ParamKind.Text;

    public bool IsMultiline => Kind is ParamKind.MultilineText or ParamKind.TextList or ParamKind.TextMap;

    /// <summary>Scripts and bodies: monospace, 8 lines (UX §5.4).</summary>
    public bool IsCode => Kind is ParamKind.MultilineText;

    public bool IsFile => Kind is ParamKind.FilePath;

    public bool IsNumber => Kind is ParamKind.WholeNumber or ParamKind.Duration;

    /// <summary>NumberBox suffix: durations are edited in seconds.</summary>
    public string Unit => Kind == ParamKind.Duration ? "s" : "";

    public double Minimum => Spec.Min ?? 0;

    public double Maximum => Spec.Max ?? (Kind == ParamKind.Duration ? 86400 : int.MaxValue);

    public bool IsBoolean => Kind is ParamKind.Boolean;

    public bool IsChoice => Kind is ParamKind.Choice;

    public bool IsDevice => Kind is ParamKind.LibraryRef;

    public IReadOnlyList<string> Choices => Spec.Choices;

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
    public string AutomationName => Error is null ? Title : $"{Title}. {Error}";

    /// <summary>A device row was picked that isn't a Library key yet (from Windows, capture): the inspector handles it.</summary>
    internal event Action<ParamFieldViewModel, DeviceOption>? DevicePicked;

    /// <summary>Shows the step's value and problem without writing anything back.</summary>
    internal void Load(ParamValue? value, string? problem)
    {
        _loading = true;
        try
        {
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
                    Device = Devices.FirstOrDefault(o => o.Key == scalar && o.Source != DeviceSource.Windows)
                        ?? Devices.FirstOrDefault(o => o.Source == DeviceSource.None);
                    break;
                case ParamKind.TextList:
                    Text = value switch
                    {
                        ListParam list => string.Join(Environment.NewLine, list.Items),
                        _ => scalar ?? "",
                    };
                    break;
                case ParamKind.TextMap:
                    Text = value is MapParam map ? string.Join(Environment.NewLine, map.Entries.Select(e => $"{e.Key}: {e.Value}")) : "";
                    break;
                default:
                    Text = scalar ?? "";
                    break;
            }
            Error = problem ?? FileProblem();
            OnPropertyChanged(nameof(AutomationName));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Replaces the picker's rows, keeping the selection on the same key.</summary>
    internal void SetDevices(IReadOnlyList<DeviceOption> options, string? current)
    {
        _loading = true;
        try
        {
            if (!Devices.SequenceEqual(options))
            {
                Devices.Clear();
                foreach (var option in options)
                    Devices.Add(option);
            }
            Device = Devices.FirstOrDefault(o => o.Key == current && o.Source != DeviceSource.Windows)
                ?? Devices.FirstOrDefault(o => o.Source == DeviceSource.None);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnTextChanged(string value)
    {
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
            ParamKind.MultilineText => Scalar(value.Trim().Length == 0 ? "" : value.ReplaceLineEndings("\n").TrimEnd()),
            _ => Scalar(value.Trim()),
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
        if (!_loading && value is not null)
            _write(this, Scalar(value));
    }

    partial void OnDeviceChanged(DeviceOption? value)
    {
        if (_loading || value is null)
            return;
        switch (value.Source)
        {
            case DeviceSource.None:
                _write(this, null);
                break;
            case DeviceSource.Library:
            case DeviceSource.Missing:
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
        if (Kind != ParamKind.FilePath || Text.Trim() is not { Length: > 0 } path)
            return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        return Path.IsPathFullyQualified(path) && !_fileExists(path) ? "Can't find this file." : null;
    }
}
