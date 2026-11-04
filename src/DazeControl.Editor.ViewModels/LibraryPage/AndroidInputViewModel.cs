using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Model;

namespace DazeControl.Editor.ViewModels.LibraryPage;

/// <summary>One D-pad move of an input, in send order; × takes it out.</summary>
public sealed record MoveToken(AndroidInputViewModel Input, int Index, string Move)
{
    public string Text => Move == AndroidInputEdits.Up ? "Up" : "Down";

    /// <summary>ChevronUp / ChevronDown.</summary>
    public string Glyph => Move == AndroidInputEdits.Up ? "" : "";

    public string RemoveName => $"Remove move {Index + 1}, {Text}, from {Input.Name}";

    public override string ToString() => Text;
}

/// <summary>
/// Library › Android › Inputs (L1, D-078): an input of an Android device, the D-pad moves from where the TV's input
/// list opens to it, and the flows whose steps pick it. Edits go through <see cref="LibraryPageViewModel"/>.
/// </summary>
public sealed partial class AndroidInputViewModel : ObservableObject
{
    internal AndroidInputViewModel(string deviceKey, string name)
    {
        DeviceKey = deviceKey;
        Name = name;
    }

    public string DeviceKey { get; }

    public string Name { get; }

    public string RenameName => $"Rename input {Name}";

    public string RemoveName => $"Remove input {Name}";

    public string AddUpName => $"Add Up to {Name}";

    public string AddDownName => $"Add Down to {Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MovesText), nameof(HasNoMoves), nameof(AutomationName))]
    public partial IReadOnlyList<MoveToken> Moves { get; internal set; } = [];

    public bool HasNoMoves => Moves.Count == 0;

    public string MovesName => $"Moves to {Name}";

    /// <summary>"Down, Down" or, with no moves, where the list opens.</summary>
    public string MovesText => Moves.Count == 0 ? "First in the list: no moves" : string.Join(", ", Moves.Select(m => m.Text));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(RemoveTip), nameof(UsedByText), nameof(AutomationName))]
    public partial int UsedBy { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool CanEdit { get; internal set; }

    public bool CanRemove => CanEdit && UsedBy == 0;

    /// <summary>Why Remove is off (UX §5.7 voice); the tooltip sits on a wrapper because a disabled button shows none.</summary>
    public string RemoveTip => UsedBy switch
    {
        0 => "Remove this input",
        1 => "Used by 1 flow. Change that step first.",
        _ => $"Used by {UsedBy} flows. Change those steps first.",
    };

    public string UsedByText => UsedBy switch
    {
        0 => "Not used yet",
        1 => "Used by 1 flow",
        _ => $"Used by {UsedBy} flows",
    };

    /// <summary>Why the last rename didn't happen, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    public partial string? RenameError { get; internal set; }

    public bool HasRenameError => RenameError is not null;

    public string AutomationName => $"{Name}, {MovesText}, {UsedByText}";

    internal void Load(IReadOnlyList<string> moves, int usedBy, bool canEdit)
    {
        if (!Moves.Select(m => m.Move).SequenceEqual(moves))
            Moves = [.. moves.Select((m, i) => new MoveToken(this, i, m))];
        UsedBy = usedBy;
        CanEdit = canEdit;
    }
}
