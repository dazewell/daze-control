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

/// <summary>One screen of a saved layout, as the Displays details show it.</summary>
public sealed record ScreenDetail(string Name, string Port, string Resolution, string Position, string Refresh, bool Primary)
{
    public string PrimaryText => Primary ? "Primary" : "";

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
    }

    /// <summary>The key steps reference; a program's path.</summary>
    public string Key { get; }

    public string Glyph { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(TestName), nameof(RemoveName), nameof(RenameName), nameof(DetailsName))]
    public partial string Title { get; internal set; } = "";

    public string TestName => $"Test {Title}";

    public string RemoveName => $"Remove {Title} from Library";

    public string RenameName => $"Rename {Title}";

    public string DetailsName => $"Details of {Title}";

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
    [NotifyPropertyChangedFor(nameof(UsedByText), nameof(CanRemove), nameof(AutomationName), nameof(IsUsed), nameof(UsedByName))]
    public partial int UsedBy { get; internal set; }

    public bool IsUsed => UsedBy > 0;

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
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool Removable { get; internal set; }

    public bool CanRemove => Removable && UsedBy == 0;

    /// <summary>Displays and Android devices, whose key is their name; flows that use it follow the new key.</summary>
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
