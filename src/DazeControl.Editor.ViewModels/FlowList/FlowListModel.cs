using DazeControl.Core.Model;

namespace DazeControl.Editor.ViewModels.FlowList;

/// <summary>One line of the flow list: a group header or a flow.</summary>
public abstract record FlowListRow;

/// <summary>A group (= a tray folder, D-087). <paramref name="FlowIds"/> are its members in list order.</summary>
public sealed record GroupRow(string Name, IReadOnlyList<string> FlowIds) : FlowListRow;

/// <summary>A flow; <paramref name="Group"/> is its group's name, null at the top level.</summary>
public sealed record FlowRow(string FlowId, string? Group) : FlowListRow;

/// <summary>Views of the list that don't change the file unless <see cref="FlowListModel.MakeOrder"/> writes them (D-088).</summary>
public enum FlowSort
{
    /// <summary>The file / tray order.</summary>
    Yours,
    Name,
    LastRun,
}

/// <summary>
/// The flow list as pure data (D-087, D-088): the order the tray menu shows, and the operations that rewrite it. A group
/// is <c>tray.folder</c>; it stands at its first visible member's place (as <c>TrayMenu.Build</c> puts it), and every
/// operation writes the displayed order back to <c>flows:</c>, so a hand-scattered group becomes contiguous without the
/// tray changing. Operations return a new <see cref="Configuration"/> that differs only in flow order and
/// <c>tray.folder</c>; unknown ids and no-op requests return the same instance.
/// </summary>
public static class FlowListModel
{
    public const int MaxGroupNameLength = 60;

    /// <summary>Tray folder names compare like the tray does.</summary>
    private static readonly StringComparer Names = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>A top-level flow (<see cref="Flow"/>) or a group (<see cref="Name"/> + <see cref="Members"/>).</summary>
    private sealed class Slot
    {
        public Flow? Flow { get; init; }
        public string? Name { get; set; }
        public List<Flow> Members { get; } = [];
        public bool IsGroup => Flow is null;
        public bool Holds(string id) => Flow is not null ? Flow.Id == id : Members.Exists(m => m.Id == id);
    }

    public static bool IsValidGroupName(string? name) =>
        name is not null && name.Trim() is { Length: > 0 and <= MaxGroupNameLength };

    /// <summary>The list: each group header followed by its members, top-level flows between them. Hidden flows are listed too.</summary>
    public static IReadOnlyList<FlowListRow> Rows(Configuration config, FlowSort sort = FlowSort.Yours,
        Func<string, DateTimeOffset?>? lastRun = null)
    {
        var rows = new List<FlowListRow>();
        foreach (var slot in Sorted(Layout(config), sort, lastRun))
        {
            if (!slot.IsGroup)
            {
                rows.Add(new FlowRow(slot.Flow!.Id, null));
                continue;
            }
            rows.Add(new GroupRow(slot.Name!, slot.Members.ConvertAll(m => m.Id)));
            rows.AddRange(slot.Members.Select(m => new FlowRow(m.Id, slot.Name)));
        }
        return rows;
    }

    /// <summary>
    /// Moves <paramref name="ids"/> (kept in their list order) into <paramref name="targetGroup"/> (null = top level; a new
    /// name creates the group) before <paramref name="beforeId"/>. Into a group the anchor must be one of its members; at
    /// the top level a member of another group stands for that whole group, so "before a group" is its first member's id.
    /// Null or not found = at the end.
    /// </summary>
    public static Configuration Move(Configuration config, IReadOnlyCollection<string> ids, string? targetGroup, string? beforeId)
    {
        var slots = Layout(config);
        var moving = ids.ToHashSet();
        if (!slots.Exists(s => Moved(s, moving)))
            return config;
        var group = targetGroup is null ? null : Normalize(targetGroup);
        if (targetGroup is not null && group is null)
            return config;

        var existing = group is null ? null : slots.Find(s => s.IsGroup && Names.Equals(s.Name, group));
        var anchorFlow = existing?.Members.Find(m => m.Id == beforeId && !moving.Contains(m.Id));
        var moved = Extract(slots, moving, beforeId, out var topAnchor);
        if (group is null)
        {
            Insert(slots, topAnchor, [.. moved.Select(f => new Slot { Flow = f })]);
        }
        else if (existing is not null && existing.Members.Count > 0)
        {
            var at = anchorFlow is null ? -1 : existing.Members.IndexOf(anchorFlow);
            existing.Members.InsertRange(at < 0 ? existing.Members.Count : at, moved);
        }
        else
        {
            var created = new Slot { Name = group };
            created.Members.AddRange(moved);
            Insert(slots, topAnchor, [created]);
        }
        return Write(config, slots);
    }

    /// <summary>
    /// Puts <paramref name="ids"/> into group <paramref name="name"/>, or out of their groups when null: a flow that leaves
    /// stands just after its former group. A new group appears at the first moved flow's place; an existing one gets them at its end.
    /// </summary>
    public static Configuration SetGroup(Configuration config, IReadOnlyCollection<string> ids, string? name)
    {
        var slots = Layout(config);
        var moving = ids.ToHashSet();
        var first = slots.FindIndex(s => Moved(s, moving));
        if (first < 0)
            return config;

        if (name is null)
        {
            var result = new List<Slot>();
            var changed = false;
            foreach (var slot in slots)
            {
                if (!slot.IsGroup || !slot.Members.Exists(m => moving.Contains(m.Id)))
                {
                    result.Add(slot);
                    continue;
                }
                changed = true;
                var leaving = slot.Members.Where(m => moving.Contains(m.Id)).ToList();
                slot.Members.RemoveAll(m => moving.Contains(m.Id));
                if (slot.Members.Count > 0)
                    result.Add(slot);
                result.AddRange(leaving.Select(f => new Slot { Flow = f }));
            }
            return changed ? Write(config, result) : config;
        }

        var group = Normalize(name);
        if (group is null)
            return config;
        var originalAt = slots.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        var moved = Extract(slots, moving, null, out _);
        var target = slots.Find(s => s.IsGroup && Names.Equals(s.Name, group));
        if (target is not null)
        {
            target.Members.AddRange(moved);
        }
        else
        {
            target = new Slot { Name = group };
            target.Members.AddRange(moved);
            // After every surviving slot that stood at or before the first moved one (that one itself, if it's a partly emptied group).
            slots.Insert(slots.Count(s => originalAt[s] <= first), target);
        }
        return Write(config, slots);
    }

    /// <summary>Moves a group as a block before the slot that holds <paramref name="beforeId"/> (null / not found = end).</summary>
    public static Configuration MoveGroup(Configuration config, string name, string? beforeId)
    {
        var slots = Layout(config);
        var group = slots.Find(s => s.IsGroup && Names.Equals(s.Name, name));
        if (group is null)
            return config;
        var anchorAt = beforeId is null ? -1 : slots.FindIndex(s => s.Holds(beforeId));
        var anchor = anchorAt < 0 ? null : slots.Skip(anchorAt).FirstOrDefault(s => !ReferenceEquals(s, group));
        slots.Remove(group);
        Insert(slots, anchor, [group]);
        return Write(config, slots);
    }

    /// <summary>Renames a group; onto an existing group's name it merges into the one that stands first. Invalid names change nothing.</summary>
    public static Configuration RenameGroup(Configuration config, string oldName, string newName)
    {
        var name = Normalize(newName);
        var slots = Layout(config);
        var group = slots.Find(s => s.IsGroup && Names.Equals(s.Name, oldName));
        if (name is null || group is null)
            return config;
        var other = slots.Find(s => s.IsGroup && !ReferenceEquals(s, group) && Names.Equals(s.Name, name));
        if (other is null)
        {
            group.Name = name;
        }
        else
        {
            var keep = slots.IndexOf(group) < slots.IndexOf(other) ? group : other;
            var drop = ReferenceEquals(keep, group) ? other : group;
            keep.Members.AddRange(drop.Members);
            keep.Name = name;
            slots.Remove(drop);
        }
        return Write(config, slots);
    }

    /// <summary>Dissolves a group; its members stay where they stand, at the top level.</summary>
    public static Configuration Ungroup(Configuration config, string name)
    {
        var slots = Layout(config);
        var at = slots.FindIndex(s => s.IsGroup && Names.Equals(s.Name, name));
        if (at < 0)
            return config;
        var members = slots[at].Members;
        slots.RemoveAt(at);
        slots.InsertRange(at, members.Select(f => new Slot { Flow = f }));
        return Write(config, slots);
    }

    /// <summary>
    /// A copy of <paramref name="id"/> right after it (D-090): "X copy", a unique id (D-075), same steps and settings, no
    /// shortcuts (they'd clash, D-072), no line above, turned on. <paramref name="newId"/> is null when the id is unknown.
    /// </summary>
    public static Configuration Duplicate(Configuration config, string id, out string? newId)
    {
        newId = null;
        var slots = Layout(config);
        var slot = slots.Find(s => s.Holds(id));
        var original = slot?.Flow ?? slot?.Members.Find(m => m.Id == id);
        if (slot is null || original is null)
            return config;

        var names = config.Flows.Select(f => f.Name).ToHashSet(Names);
        var name = $"{original.Name} copy";
        for (var n = 2; names.Contains(name); n++)
            name = $"{original.Name} copy {n}";
        newId = FlowIds.Unique(FlowIds.Slug(name), config.Flows.Select(f => f.Id).ToHashSet());
        var copy = original with
        {
            Id = newId,
            Name = name,
            Hotkeys = [],
            Enabled = true,
            Tray = original.Tray with { Separator = false },
        };
        if (slot.IsGroup)
            slot.Members.Insert(slot.Members.IndexOf(original) + 1, copy);
        else
            slots.Insert(slots.IndexOf(slot) + 1, new Slot { Flow = copy });
        return Write(config, slots);
    }

    /// <summary>"Make this my order": writes what <see cref="Rows"/> shows for <paramref name="sort"/>.</summary>
    public static Configuration MakeOrder(Configuration config, FlowSort sort, Func<string, DateTimeOffset?>? lastRun = null) =>
        Write(config, Sorted(Layout(config), sort, lastRun));

    // ---- internals ----

    private static string? Normalize(string? name) => IsValidGroupName(name) ? name!.Trim() : null;

    private static bool Moved(Slot slot, HashSet<string> ids) =>
        slot.IsGroup ? slot.Members.Exists(m => ids.Contains(m.Id)) : ids.Contains(slot.Flow!.Id);

    /// <summary>Tray order: a group stands at its first visible member's place, else its first member's.</summary>
    private static List<Slot> Layout(Configuration config)
    {
        var order = new List<(int At, Slot Slot)>();
        var groups = new Dictionary<string, Slot>(Names);
        var visibleAt = new Dictionary<Slot, int>();
        var index = 0;
        foreach (var flow in config.Flows)
        {
            if (flow.Tray.Folder is { Length: > 0 } folder)
            {
                if (!groups.TryGetValue(folder, out var slot))
                {
                    slot = new Slot { Name = folder };
                    groups[folder] = slot;
                    order.Add((index, slot));
                }
                slot.Members.Add(flow);
                if (flow.Enabled && flow.Tray.Show && visibleAt.TryAdd(slot, index))
                    slot.Name = folder; // the tray spells a folder like its first visible member
            }
            else
            {
                order.Add((index, new Slot { Flow = flow }));
            }
            index++;
        }
        return [.. order.OrderBy(o => visibleAt.GetValueOrDefault(o.Slot, o.At)).Select(o => o.Slot)];
    }

    private static List<Slot> Sorted(List<Slot> slots, FlowSort sort, Func<string, DateTimeOffset?>? lastRun)
    {
        if (sort == FlowSort.Yours)
            return slots;

        List<Flow> Order(IEnumerable<Flow> flows) => sort == FlowSort.Name
            ? [.. flows.OrderBy(f => f.Name, Names)]
            : [.. flows.OrderByDescending(f => lastRun?.Invoke(f.Id) ?? DateTimeOffset.MinValue)];

        // Groups keep their place; top-level flows swap among the top-level places.
        var top = Order(slots.Where(s => !s.IsGroup).Select(s => s.Flow!));
        var next = 0;
        var result = new List<Slot>();
        foreach (var slot in slots)
        {
            if (!slot.IsGroup)
            {
                result.Add(new Slot { Flow = top[next++] });
                continue;
            }
            var sorted = new Slot { Name = slot.Name };
            sorted.Members.AddRange(Order(slot.Members));
            result.Add(sorted);
        }
        return result;
    }

    /// <summary>
    /// Takes <paramref name="moving"/> out of <paramref name="slots"/> (dropping emptied groups) and returns them in list
    /// order. <paramref name="topAnchor"/> is the top-level slot to insert before: the first slot at or after the one holding
    /// <paramref name="beforeId"/> that survives, null = end.
    /// </summary>
    private static List<Flow> Extract(List<Slot> slots, HashSet<string> moving, string? beforeId, out Slot? topAnchor)
    {
        var anchorAt = beforeId is null ? -1 : slots.FindIndex(s => s.Holds(beforeId));
        topAnchor = anchorAt < 0 ? null : slots.Skip(anchorAt).FirstOrDefault(s => !EmptiedBy(s, moving));

        var moved = new List<Flow>();
        foreach (var slot in slots)
        {
            if (slot.IsGroup)
            {
                moved.AddRange(slot.Members.Where(m => moving.Contains(m.Id)));
                slot.Members.RemoveAll(m => moving.Contains(m.Id));
            }
            else if (moving.Contains(slot.Flow!.Id))
            {
                moved.Add(slot.Flow);
            }
        }
        slots.RemoveAll(s => s.IsGroup ? s.Members.Count == 0 : moving.Contains(s.Flow!.Id));
        return moved;
    }

    private static bool EmptiedBy(Slot slot, HashSet<string> moving) =>
        slot.IsGroup ? slot.Members.TrueForAll(m => moving.Contains(m.Id)) : moving.Contains(slot.Flow!.Id);

    private static void Insert(List<Slot> slots, Slot? anchor, List<Slot> items)
    {
        var at = anchor is null ? -1 : slots.IndexOf(anchor);
        slots.InsertRange(at < 0 ? slots.Count : at, items);
    }

    /// <summary>Flattens the slots into <c>flows:</c>; group members get the group's spelling as <c>tray.folder</c>.</summary>
    private static Configuration Write(Configuration config, List<Slot> slots)
    {
        var flows = new List<Flow>(config.Flows.Count);
        foreach (var slot in slots)
        {
            if (!slot.IsGroup)
            {
                // A flow that left its group no longer names it.
                flows.Add(slot.Flow!.Tray.Folder is { Length: > 0 } ? slot.Flow with { Tray = slot.Flow.Tray with { Folder = null } } : slot.Flow);
                continue;
            }
            flows.AddRange(slot.Members.Select(m =>
                m.Tray.Folder == slot.Name ? m : m with { Tray = m.Tray with { Folder = slot.Name } }));
        }
        var written = new ValueList<Flow>(flows);
        return written == config.Flows ? config : config with { Flows = written };
    }
}
