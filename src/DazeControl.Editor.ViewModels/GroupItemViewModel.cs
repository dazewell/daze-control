using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DazeControl.Editor.ViewModels;

/// <summary>
/// A group header row of the Flows list (D-087): a tray folder with its members. Built and updated by
/// <see cref="FlowsPageViewModel"/>; collapsing hides the member rows, not the flows.
/// </summary>
public sealed partial class GroupItemViewModel : ObservableObject
{
    private readonly FlowsPageViewModel _page;
    private IReadOnlyList<FlowItemViewModel> _members = [];

    internal GroupItemViewModel(FlowsPageViewModel page, string name)
    {
        _page = page;
        Name = name;
    }

    /// <summary>The members this header stands for (the ones a search shows).</summary>
    internal IReadOnlyList<FlowItemViewModel> Members => _members;

    /// <summary>All members are selected (D-090).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; private set; }

    /// <summary>The Select toggle is on: the header shows a checkbox.</summary>
    [ObservableProperty]
    public partial bool ShowCheck { get; private set; }

    internal void UpdateSelection(bool selected, bool showCheck)
    {
        IsSelected = selected;
        ShowCheck = showCheck;
    }

    /// <summary>The id a drop "before this group" names (a group stands for its first member).</summary>
    public string? FirstMemberId => _members.Count > 0 ? _members[0].Id : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleName), nameof(Summary))]
    public partial string Name { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleName), nameof(ChevronGlyph), nameof(Summary))]
    public partial bool IsExpanded { get; private set; } = true;

    /// <summary>Chevron down while expanded, right while collapsed.</summary>
    public string ChevronGlyph => IsExpanded ? "" : "";

    /// <summary>The chevron button's UIA name.</summary>
    public string ToggleName => $"{(IsExpanded ? "Collapse" : "Expand")} {Name}";

    /// <summary>"4 flows · 1 running" (just "4 flows" when none runs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Caption { get; private set; } = "";

    /// <summary>"Audio, group, 4 flows, expanded": the header's UIA name.</summary>
    public string Summary => $"{Name}, group, {Caption.Split(" · ")[0]}, {(IsExpanded ? "expanded" : "collapsed")}";

    /// <summary>"2 flows need fixing"; empty when none does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial string Problems { get; private set; } = "";

    public bool HasProblems => Problems.Length > 0;

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    public partial bool CanReorder { get; private set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; private set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; private set; }

    [RelayCommand]
    private void Toggle() => _page.ToggleGroup(Name);

    internal void Update(string name, IReadOnlyList<FlowItemViewModel> members, bool expanded, bool canEdit, bool canMoveUp, bool canMoveDown, bool canReorder)
    {
        Name = name;
        _members = members;
        IsExpanded = expanded;
        CanEdit = canEdit;
        CanReorder = canReorder;
        CanMoveUp = canEdit && canMoveUp;
        CanMoveDown = canEdit && canMoveDown;
        Refresh();
    }

    /// <summary>Recounts the running and failing members (their state changes on run events, not only on config changes).</summary>
    internal void Refresh()
    {
        var running = _members.Count(m => m.IsRunning);
        var count = _members.Count;
        Caption = $"{count} {(count == 1 ? "flow" : "flows")}" + (running > 0 ? $" · {running} running" : "");
        var bad = _members.Count(m => m.HasProblems);
        Problems = bad switch { 0 => "", 1 => "1 flow needs fixing", _ => $"{bad} flows need fixing" };
    }
}
