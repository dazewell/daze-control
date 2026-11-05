namespace DazeControl.Editor.ViewModels.FlowList;

/// <summary>One visible row for the drop maths: a group header (<see cref="Key"/> = its name) or a flow (its id, and its group).</summary>
public readonly record struct DropRow(bool IsHeader, string Key, string? Group, string? FirstMemberId = null);

/// <summary>
/// Where a drop would land (DESIGN B2): the group and the anchor to give <see cref="FlowListModel.Move"/> /
/// <see cref="FlowListModel.MoveGroup"/>, and where to draw the cue. <see cref="LineIndex"/> is the visible row the 2-px line
/// goes above (the row count = below the last); <see cref="Indented"/> when it lands inside a group;
/// <see cref="HighlightGroup"/> when the whole group header takes the drop instead of a line.
/// </summary>
public sealed record DropTarget(string? Group, string? BeforeId, int LineIndex, bool Indented, string? HighlightGroup);

/// <summary>Pure drop-position maths of the Flows list (T16.4); the window only measures the pointer.</summary>
public static class DropMath
{
    /// <summary>
    /// Resolves a pointer over row <paramref name="over"/>, <paramref name="fraction"/> (0 = top edge, 1 = bottom) down it.
    /// <paramref name="insideIndent"/>: the pointer is right of the group's indent (decides after a group's last member).
    /// <paramref name="draggingGroup"/> true = a whole group is dragged (top level only). Null = nowhere to drop.
    /// </summary>
    public static DropTarget? Resolve(IReadOnlyList<DropRow> rows, int over, double fraction, bool insideIndent, bool draggingGroup)
    {
        if (over < 0 || over >= rows.Count)
            return null;
        var row = rows[over];
        return draggingGroup ? ResolveGroup(rows, over, row, fraction) : ResolveFlow(rows, over, row, fraction, insideIndent);
    }

    private static DropTarget ResolveFlow(IReadOnlyList<DropRow> rows, int over, DropRow row, double fraction, bool insideIndent)
    {
        if (row.IsHeader)
        {
            var expanded = over + 1 < rows.Count && !rows[over + 1].IsHeader && Same(rows[over + 1].Group, row.Key);
            if (fraction < 0.25)
                return new DropTarget(null, row.FirstMemberId, over, false, null);
            if (fraction >= 0.75 && expanded)
                return new DropTarget(row.Key, rows[over + 1].Key, over + 1, true, null);
            return new DropTarget(row.Key, null, -1, false, row.Key); // the middle: last in the group, open or collapsed
        }
        if (fraction < 0.5)
            return new DropTarget(row.Group, row.Key, over, row.Group is not null, null);

        var next = over + 1 < rows.Count ? rows[over + 1] : (DropRow?)null;
        if (row.Group is { } group)
        {
            if (next is { IsHeader: false } member && Same(member.Group, group))
                return new DropTarget(group, member.Key, over + 1, true, null);
            if (insideIndent)
                return new DropTarget(group, null, over + 1, true, null); // last in its group
            return AfterGroup(rows, over + 1);
        }
        return next is null ? new DropTarget(null, null, rows.Count, false, null) : new DropTarget(null, Anchor(next.Value), over + 1, false, null);
    }

    private static DropTarget ResolveGroup(IReadOnlyList<DropRow> rows, int over, DropRow row, double fraction)
    {
        // A group moves among the top-level items: a row of another group counts as that group.
        var start = over;
        while (start > 0 && !rows[start].IsHeader && rows[start].Group is not null)
            start--;
        return fraction < 0.5 ? new DropTarget(null, Anchor(rows[start]), start, false, null) : AfterGroup(rows, over + 1);
    }

    /// <summary>After the group whose rows end before <paramref name="from"/>: before the next top-level item.</summary>
    private static DropTarget AfterGroup(IReadOnlyList<DropRow> rows, int from)
    {
        var i = from;
        while (i < rows.Count && !rows[i].IsHeader && rows[i].Group is not null)
            i++; // the rest of this group's members
        return i < rows.Count ? new DropTarget(null, Anchor(rows[i]), i, false, null) : new DropTarget(null, null, rows.Count, false, null);
    }

    /// <summary>The id <see cref="FlowListModel"/> takes for "before this top-level row": a group stands for its first member.</summary>
    private static string? Anchor(DropRow row) => row.IsHeader ? row.FirstMemberId : row.Key;

    private static bool Same(string? a, string? b) => StringComparer.CurrentCultureIgnoreCase.Equals(a, b);
}
