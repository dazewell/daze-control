using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DazeControl.Editor.ViewModels.LibraryPage;

public enum TestOutcome
{
    Worked,

    /// <summary>Found, but a run may still not get far ("Not connected right now. The flow will still try.").</summary>
    Caution,
    Failed,
}

/// <summary>A test's sentence and how it went (UX §5.7 #11, #12).</summary>
public sealed record TestResult(TestOutcome Outcome, string Message);

/// <summary>A flow in a "Used by" list; opening it jumps to the flow editor.</summary>
public sealed record FlowLink(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A Library item in a bound device's side pickers (D-116).</summary>
public sealed record LibraryChoice(string Key, string Title)
{
    public override string ToString() => Title;
}

/// <summary>One screen of a saved layout, as the Displays details show it.</summary>
public sealed record ScreenDetail(string Name, string Port, string Resolution, string Position, string Refresh, bool Primary)
{
    public string PrimaryText => Primary ? "Primary" : "";

    /// <summary>"HDMI-1 · 2560 × 1440 · 60 Hz · at 0, 0 · Primary": one line that wraps in a narrow window.</summary>
    public string Summary =>
        Captions.Join(Port, Resolution, Refresh, $"at {Position}", PrimaryText);

    public override string ToString() =>
        string.Join(", ", new[] { Name, Port, Resolution, Refresh, $"at {Position}", PrimaryText }.Where(t => t.Length > 0));
}

/// <summary>One settings card on the Library page: what it is, its live state, "Used by 2 flows", Test.</summary>
public sealed partial class LibraryItemViewModel : ObservableObject
{
    private readonly Func<LibraryItemViewModel, CancellationToken, Task<TestResult>> _test;

    internal LibraryItemViewModel(string key, string glyph, Func<LibraryItemViewModel, CancellationToken, Task<TestResult>> test)
    {
        Key = key;
        Glyph = glyph;
        _test = test;
        Inputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoInputs));
    }

    /// <summary>The key steps reference; a program's path.</summary>
    public string Key { get; }

    public string Glyph { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(TestName), nameof(RemoveName), nameof(RenameName), nameof(SidesName), nameof(DetailsName), nameof(OpenLocationName), nameof(InputsName))]
    public partial string Title { get; internal set; } = "";

    public string TestName => $"Test {Title}";

    /// <summary>Includes why Remove is off, so a screen reader hears it on the disabled button too (F-011).</summary>
    public string RemoveName => CanRemove ? $"Remove {Title} from Library" : $"Remove {Title} from Library, not available. {RemoveTip}";

    public string RenameName => $"Rename {Title}";

    public string DetailsName => $"Details of {Title}";

    public string OpenLocationName => $"Open file location of {Title}";

    public string UsedByName => $"{UsedByText}: show them";

    /// <summary>"Output", the Bluetooth address, the program's folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(AutomationName))]
    public partial string Detail { get; internal set; } = "";

    /// <summary>Live: "Connected", "Not found on this PC"; "" while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(AutomationName))]
    public partial string State { get; internal set; } = "";

    public string Description => State.Length == 0 ? Detail : Detail.Length == 0 ? State : $"{Detail} · {State}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedByText), nameof(CanRemove), nameof(RemoveTip), nameof(RemoveName), nameof(AutomationName), nameof(IsUsed), nameof(IsUnused), nameof(UsedByName))]
    public partial int UsedBy { get; internal set; }

    public bool IsUsed => UsedBy > 0;

    public bool IsUnused => UsedBy == 0;

    /// <summary>The flows behind <see cref="UsedBy"/>, by name, for the "Used by" flyout.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FlowLink> UsedByFlows { get; internal set; } = [];

    public string UsedByText => UsedBy switch
    {
        0 => "Not used yet",
        1 => "Used by 1 flow",
        _ => $"Used by {UsedBy} flows",
    };

    /// <summary>The Library's problem with this item (a damaged layout, a missing address), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; internal set; }

    public bool HasProblem => Problem is not null;

    /// <summary>Programs aren't stored on their own; a device in use would leave its flows broken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(RemoveName))]
    public partial bool Removable { get; internal set; }

    public bool CanRemove => Removable && UsedBy == 0 && BoundIn is null;

    /// <summary>A Bluetooth or audio device that is a side of a bound device: that device's name (D-116).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(RemoveTip), nameof(RemoveName))]
    public partial string? BoundIn { get; internal set; }

    // ---- Bound devices (D-116): which Bluetooth and audio devices it binds, changed in place ----

    /// <summary>Set for a bound device's card; called when a side picker changes.</summary>
    internal Action<LibraryItemViewModel>? SidesChanged { get; init; }

    public bool IsBound => SidesChanged is not null;

    private bool _syncingSides;

    [ObservableProperty]
    public partial bool ShowSides { get; set; }

    [ObservableProperty]
    public partial bool CanEditSides { get; internal set; }

    [ObservableProperty]
    public partial IReadOnlyList<LibraryChoice> BluetoothChoices { get; internal set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<LibraryChoice> AudioChoices { get; internal set; } = [];

    [ObservableProperty]
    public partial LibraryChoice? BoundBluetooth { get; set; }

    [ObservableProperty]
    public partial LibraryChoice? BoundAudio { get; set; }

    public string SidesName => $"Devices of {Title}";

    internal void LoadSides(IReadOnlyList<LibraryChoice> bluetooth, IReadOnlyList<LibraryChoice> audio, LibraryChoice? boundBluetooth,
        LibraryChoice? boundAudio, bool canEdit)
    {
        _syncingSides = true;
        try
        {
            BluetoothChoices = bluetooth;
            AudioChoices = audio;
            BoundBluetooth = boundBluetooth;
            BoundAudio = boundAudio;
            CanEditSides = canEdit;
        }
        finally
        {
            _syncingSides = false;
        }
    }

    partial void OnBoundBluetoothChanged(LibraryChoice? value) => OnSideChanged(value);

    partial void OnBoundAudioChanged(LibraryChoice? value) => OnSideChanged(value);

    private void OnSideChanged(LibraryChoice? value)
    {
        if (!_syncingSides && value is not null)
            SidesChanged?.Invoke(this);
    }

    /// <summary>Why Remove is off while a flow uses it (UX §5.7 voice); the button always shows, so rows line up.</summary>
    public string RemoveTip => UsedBy switch
    {
        0 when BoundIn is not null => $"Part of {BoundIn}. Remove that bound device first.",
        0 => "Remove from Library",
        1 => "Used by 1 flow. Remove it from that flow first.",
        _ => $"Used by {UsedBy} flows. Remove it from those flows first.",
    };

    /// <summary>What the rename box starts with: the name the card shows (Bluetooth, bound), or the key that is the name.</summary>
    [ObservableProperty]
    public partial string RenameText { get; internal set; } = "";

    /// <summary>Displays and Android devices (their key is their name; flows that use it follow the new key), and
    /// Bluetooth and bound devices (their display name changes, the key stays).</summary>
    [ObservableProperty]
    public partial bool CanRename { get; internal set; }

    /// <summary>Why the last rename didn't happen, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    public partial string? RenameError { get; internal set; }

    public bool HasRenameError => RenameError is not null;

    /// <summary>Programs: the file can be shown in File Explorer.</summary>
    public bool CanOpenLocation { get; internal init; }

    /// <summary>A saved layout's screens, read from the capture (names and ports from the live list when plugged in).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial IReadOnlyList<ScreenDetail> Screens { get; internal set; } = [];

    public bool HasDetails => Screens.Count > 0;

    [ObservableProperty]
    public partial bool ShowDetails { get; set; }

    /// <summary>Android devices: their inputs can be edited here (L1, D-078).</summary>
    public bool HasInputs { get; internal init; }

    /// <summary>Android: the inputs, in the order the file keeps them.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AndroidInputViewModel> Inputs { get; } = [];

    /// <summary>Android: the Inputs panel under the card is open.</summary>
    [ObservableProperty]
    public partial bool ShowInputs { get; set; }

    public string InputsName => $"Inputs of {Title}";

    /// <summary>Android: why the last Add input didn't happen, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddInputError))]
    public partial string? AddInputError { get; internal set; }

    public bool HasAddInputError => AddInputError is not null;

    /// <summary>Android: the name Add input offers ("input", "input-2"…).</summary>
    [ObservableProperty]
    public partial string NewInputName { get; internal set; } = "";

    [ObservableProperty]
    public partial bool CanEditInputs { get; internal set; }

    public bool HasNoInputs => Inputs.Count == 0;


    public string AutomationName => $"{Title}, {Description}, {UsedByText}".Replace(", , ", ", ", StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    public partial bool IsTesting { get; private set; }

    public bool CanTest => !IsTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ResultMessage), nameof(Worked), nameof(Cautioned), nameof(Failed))]
    public partial TestResult? Result { get; private set; }

    public bool HasResult => Result is not null;

    public string ResultMessage => Result?.Message ?? "";

    public bool Worked => Result?.Outcome == TestOutcome.Worked;

    public bool Cautioned => Result?.Outcome == TestOutcome.Caution;

    public bool Failed => Result?.Outcome == TestOutcome.Failed;

    /// <summary>Read-only: looks for the device or file, never changes it.</summary>
    [RelayCommand]
    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (IsTesting)
            return;
        IsTesting = true;
        Result = null;
        try
        {
            Result = await _test(this, cancellationToken).ConfigureAwait(true); // back on the UI thread
        }
        catch (OperationCanceledException)
        {
            Result = null;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void DismissResult() => Result = null;

    internal void ShowResult(TestResult result) => Result = result;
}
