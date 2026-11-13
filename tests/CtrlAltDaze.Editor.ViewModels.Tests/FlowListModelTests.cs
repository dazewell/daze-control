using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Editor.ViewModels.FlowList;
using CtrlAltDaze.Runtime;
using CtrlAltDaze.Runtime.Tray;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

public sealed class FlowListModelTests
{
    /// <summary>"a", "b:TV" (folder), "c!" (line above), "d-" (hidden from the tray), "e~" (turned off).</summary>
    private static Configuration Make(params string[] specs)
    {
        var yaml = "flows:\n" + string.Concat(specs.Select(spec =>
        {
            var parts = spec.Split(':');
            var id = parts[0].TrimEnd('!', '-', '~');
            var tray = "";
            if (parts.Length > 1)
                tray += $"folder: {parts[1]}, ";
            if (parts[0].EndsWith('!'))
                tray += "separator: true, ";
            if (parts[0].EndsWith('-'))
                tray += "show: false, ";
            var enabled = parts[0].EndsWith('~') ? "    enabled: false\n" : "";
            return $"  {id}:\n    name: {id.ToUpperInvariant()}\n    hotkeys: [Alt+{id.ToUpperInvariant()[0]}]\n{enabled}    tray: {{ {tray}}}\n" +
                   "    nodes: [{ id: n, step: notify, with: { text: x } }]\n";
        }));
        return ConfigYaml.Load(yaml).Config!;
    }

    private static string Ids(Configuration c) => string.Join(" ", c.Flows.Select(f => f.Id));

    /// <summary>The list as text: "G{TV" header, members, "}" is implicit; top-level flows plain.</summary>
    private static string List(Configuration c, FlowSort sort = FlowSort.Yours) => string.Join(" ",
        FlowListModel.Rows(c, sort).Select(r => r is GroupRow g ? $"[{g.Name}]" : ((FlowRow)r).FlowId));

    private static List<string> Tray(Configuration c)
    {
        var snapshot = new ConfigSnapshot(c.Settings, c.Library,
            new ValueList<FlowState>(c.Flows.Select(f => new FlowState(f.Id, f, []) { Written = f })), []);
        var state = new TrayState(new HashSet<string>(), _ => null, false, true);
        var lines = new List<string>();
        void Walk(IReadOnlyList<TrayEntry> entries, string indent)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case TraySeparator: lines.Add(indent + "---"); break;
                    case TrayFolder f: lines.Add(indent + "> " + f.Text); Walk(f.Items, indent + " "); break;
                    case TrayFlowItem i: lines.Add(indent + i.FlowId); break;
                }
            }
        }
        Walk(TrayMenu.Build(snapshot, state).TakeWhile(e => e is not TrayCommandItem).ToList(), "");
        if (lines.Count > 0 && lines[^1] == "---")
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>The list reduced to what the tray shows: visible flows only, groups with at least one visible member.</summary>
    private static List<string> ListAsTray(Configuration c)
    {
        var visible = c.Flows.Where(f => f.Enabled && f.Tray.Show).Select(f => f.Id).ToHashSet();
        var lines = new List<string>();
        string? group = null;
        foreach (var row in FlowListModel.Rows(c))
        {
            if (row is GroupRow g)
            {
                group = null;
                if (g.FlowIds.Any(visible.Contains))
                {
                    lines.Add("> " + g.Name);
                    group = g.Name;
                }
            }
            else if (row is FlowRow r && visible.Contains(r.FlowId) && (r.Group is null || group is not null))
            {
                lines.Add((r.Group is null ? "" : " ") + r.FlowId);
            }
        }
        return lines;
    }

    [Fact]
    public void ListOrderEqualsTrayOrderEvenWhenAGroupIsScatteredAndFlowsAreHidden()
    {
        var config = Make("a", "h-:tv", "b:tv", "c", "d:TV", "e~", "f:Audio", "g!", "i:audio", "j-");
        var tray = Tray(config);
        Assert.Equal(["a", "> tv", " b", " d", "c", "> Audio", " f", " i", "---", "g"], tray);
        Assert.Equal(tray.Where(l => l != "---"), ListAsTray(config));
    }

    [Fact]
    public void ListedGroupMembersAreFolderNamesComparedIgnoringCase()
    {
        var config = Make("f:Audio", "x", "i:AUDIO");
        Assert.Equal("[Audio] f i x", List(config));
    }

    [Fact]
    public void AGroupWithHiddenFirstMemberStandsWhereTheTrayPutsIt()
    {
        var config = Make("h-:tv", "c", "b:tv");
        Assert.Equal("c [tv] h b", List(config));
        Assert.Equal(["c", "> tv", " b"], Tray(config));
    }

    [Fact]
    public void ReorderKeepsTheTrayTheSameAndOnlyChangesOrderAndFolder()
    {
        var config = Make("a", "h-:tv", "b:tv", "c", "d:TV", "g!", "j-");
        var written = FlowListModel.MakeOrder(config, FlowSort.Yours);
        Assert.Equal(config.Flows.Count, written.Flows.Count);
        Assert.Equal(Tray(config), Tray(written));
        Assert.Equal(List(config), List(written));
        AssertOnlyOrderAndFolderChanged(config, written);
    }

    private static void AssertOnlyOrderAndFolderChanged(Configuration before, Configuration after)
    {
        Assert.Equal(before.Flows.Count, after.Flows.Count);
        foreach (var flow in before.Flows)
        {
            var other = after.Flows.Single(f => f.Id == flow.Id);
            Assert.Equal(flow with { Tray = flow.Tray with { Folder = null } }, other with { Tray = other.Tray with { Folder = null } });
        }
        Assert.Equal(before.Settings, after.Settings);
        Assert.Equal(before.Library, after.Library);
    }

    [Fact]
    public void MoveWithinTopLevelAndAcrossAGroupBlock()
    {
        var config = Make("a", "b:tv", "c:tv", "d");
        Assert.Equal("d a [tv] b c", List(FlowListModel.Move(config, ["d"], null, "a")));
        Assert.Equal("[tv] b c d a", List(FlowListModel.Move(config, ["a"], null, null)));
        // "before b" at top level means before the whole group.
        Assert.Equal("a d [tv] b c", List(FlowListModel.Move(config, ["d"], null, "b")));
    }

    [Fact]
    public void MoveIntoWithinAndOutOfAGroup()
    {
        var config = Make("a", "b:tv", "c:tv", "d");
        var into = FlowListModel.Move(config, ["a"], "TV", "c");
        Assert.Equal("[tv] b a c d", List(into));
        Assert.Equal("tv", into.Flows.Single(f => f.Id == "a").Tray.Folder);
        var within = FlowListModel.Move(config, ["c"], "tv", "b");
        Assert.Equal("a [tv] c b d", List(within));
        var outOf = FlowListModel.Move(config, ["b"], null, "d");
        Assert.Equal("a [tv] c b d", List(outOf));
        Assert.Null(outOf.Flows.Single(f => f.Id == "b").Tray.Folder);
    }

    [Fact]
    public void MoveSeveralKeepsTheirListOrder()
    {
        var config = Make("a", "b", "c", "d");
        Assert.Equal("a c b d", List(FlowListModel.Move(config, ["c", "a"], null, "b")));
        Assert.Equal("b a c d", List(FlowListModel.Move(config, ["c", "a"], null, "d")));
    }

    [Fact]
    public void MovingTheAnchorOntoItselfKeepsTheOrder()
    {
        var config = Make("a", "b", "c");
        Assert.Equal("a b c", List(FlowListModel.Move(config, ["b"], null, "b")));
    }

    [Fact]
    public void MoveToANewGroupCreatesItAndEmptyingAGroupRemovesIt()
    {
        var config = Make("a", "b:tv", "c");
        var moved = FlowListModel.Move(config, ["b"], "Audio", null);
        Assert.Equal("a c [Audio] b", List(moved));
        Assert.DoesNotContain(moved.Flows, f => f.Tray.Folder == "tv");
    }

    [Fact]
    public void MoveGroupAsABlock()
    {
        var config = Make("a", "b:tv", "c", "d:tv", "e");
        Assert.Equal("[tv] b d a c e", List(FlowListModel.MoveGroup(config, "TV", "a")));
        Assert.Equal("a c [tv] b d e", List(FlowListModel.MoveGroup(config, "tv", "e")));
        Assert.Equal("a c e [tv] b d", List(FlowListModel.MoveGroup(config, "tv", null)));
    }

    [Fact]
    public void SetGroupCreatesAtTheFirstFlowsPlaceAndAppendsToAnExistingGroup()
    {
        var config = Make("a", "b", "c", "g:Mix");
        var made = FlowListModel.SetGroup(config, ["c", "b"], "New");
        Assert.Equal("a [New] b c [Mix] g", List(made));
        var appended = FlowListModel.SetGroup(config, ["a"], "mix");
        Assert.Equal("b c [Mix] g a", List(appended));
        Assert.Equal("Mix", appended.Flows.Single(f => f.Id == "a").Tray.Folder);
    }

    [Fact]
    public void SetGroupNullTakesFlowsOutJustAfterTheirGroup()
    {
        var config = Make("a", "b:tv", "c:tv", "d:tv", "e");
        var left = FlowListModel.SetGroup(config, ["c"], null);
        Assert.Equal("a [tv] b d c e", List(left));
        Assert.Null(left.Flows.Single(f => f.Id == "c").Tray.Folder);
        Assert.Equal("a b c d e", List(FlowListModel.SetGroup(config, ["b", "c", "d"], null)));
        Assert.Same(config, FlowListModel.SetGroup(config, ["a"], null));
    }

    [Fact]
    public void RenameGroupAndMergeOntoAnExistingOne()
    {
        var config = Make("a:tv", "b", "c:Audio", "d:tv");
        var renamed = FlowListModel.RenameGroup(config, "TV", " Lounge ");
        Assert.Equal("[Lounge] a d b [Audio] c", List(renamed));
        var merged = FlowListModel.RenameGroup(config, "audio", "TV");
        Assert.Equal("[TV] a d c b", List(merged));
        Assert.Equal(["TV"], merged.Flows.Where(f => f.Id != "b").Select(f => f.Tray.Folder).Distinct());
        Assert.Same(config, FlowListModel.RenameGroup(config, "tv", "  "));
        Assert.Same(config, FlowListModel.RenameGroup(config, "tv", new string('x', 61)));
    }

    [Fact]
    public void UngroupLeavesMembersWhereTheyStand()
    {
        var config = Make("a", "b:tv", "c:tv", "d");
        var open = FlowListModel.Ungroup(config, "TV");
        Assert.Equal("a b c d", List(open));
        Assert.Equal("a b c d", Ids(open));
        Assert.All(open.Flows, f => Assert.Null(f.Tray.Folder));
    }

    [Fact]
    public void DuplicateGoesAfterTheOriginalInItsGroupWithoutShortcuts()
    {
        var config = Make("a", "b!:tv", "c:tv", "d");
        var copy = FlowListModel.Duplicate(config, "b", out var id);
        Assert.Equal("b-copy", id);
        Assert.Equal("a [tv] b b-copy c d", List(copy));
        var made = copy.Flows.Single(f => f.Id == id);
        Assert.Equal("B copy", made.Name);
        Assert.Empty(made.Hotkeys);
        Assert.False(made.Tray.Separator);
        Assert.Equal("tv", made.Tray.Folder);
        Assert.Equal(config.Flows.Single(f => f.Id == "b").Nodes, made.Nodes);

        var again = FlowListModel.Duplicate(copy, "b", out var id2);
        Assert.Equal("b-copy-2", id2);
        Assert.Equal("B copy 2", again.Flows.Single(f => f.Id == id2).Name);
        Assert.Same(config, FlowListModel.Duplicate(config, "nope", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void MakeOrderWritesTheSortWithinContainersAndGroupsKeepTheirPlace()
    {
        var config = Make("d", "z:tv", "y:tv", "b", "a");
        Assert.Equal("a [tv] y z b d", List(config, FlowSort.Name));
        var written = FlowListModel.MakeOrder(config, FlowSort.Name);
        Assert.Equal("a y z b d", Ids(written));
        Assert.Equal(List(config, FlowSort.Name), List(written));
        var runs = new Dictionary<string, DateTimeOffset> { ["b"] = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), ["d"] = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero) };
        Assert.Equal("d [tv] z y b a", string.Join(" ",
            FlowListModel.Rows(config, FlowSort.LastRun, id => runs.TryGetValue(id, out var t) ? t : null)
                .Select(r => r is GroupRow g ? $"[{g.Name}]" : ((FlowRow)r).FlowId)));
    }

    [Fact]
    public void TheWrittenOrderRoundTripsThroughYamlAndTheTrayLooksTheSame()
    {
        var config = Make("a", "h-:tv", "b:tv", "c", "d:TV", "g!", "j-");
        var moved = FlowListModel.Move(config, ["c"], "tv", "b");
        var reloaded = ConfigYaml.Load(ConfigYaml.Save(moved)).Config!;
        Assert.Equal(Ids(moved), Ids(reloaded));
        Assert.Equal(List(moved), List(reloaded));
        Assert.Equal(Tray(moved), Tray(reloaded));
        AssertOnlyOrderAndFolderChanged(config, reloaded);
    }

    [Fact]
    public void UnknownIdsAndInvalidNamesChangeNothing()
    {
        var config = Make("a", "b");
        Assert.Same(config, FlowListModel.Move(config, ["zz"], null, "a"));
        Assert.Same(config, FlowListModel.Move(config, ["a"], " ", null));
        Assert.Same(config, FlowListModel.MoveGroup(config, "none", null));
        Assert.Same(config, FlowListModel.Ungroup(config, "none"));
    }

    [Fact]
    public void AGroupIsSpelledLikeItsFirstVisibleMemberAndNoOpsReturnTheSameConfig()
    {
        var config = Make("h-:tv", "d:TV");
        Assert.Equal("[TV] h d", List(config));
        Assert.Equal(["> TV", " d"], Tray(config));
        var plain = Make("a", "b", "c");
        Assert.Same(plain, FlowListModel.Move(plain, ["b"], null, "b"));
        Assert.Same(plain, FlowListModel.MakeOrder(plain, FlowSort.Yours));
    }
}
