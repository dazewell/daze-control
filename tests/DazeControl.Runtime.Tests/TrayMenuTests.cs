using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Tray;

namespace DazeControl.Runtime.Tests;

public sealed class TrayMenuTests
{
    private static ConfigSnapshot Snapshot(string flowsYaml) =>
        ConfigHost.Apply(ConfigSnapshot.Empty, ConfigYaml.Load("flows:\n" + flowsYaml));

    private static string Flow(string id, string extra = "") =>
        $"  {id}:\n    name: {id.ToUpperInvariant()}\n{extra}    nodes: [{{ id: n, step: notify, with: {{ text: x }} }}]\n";

    private static TrayState State(bool paused = false, bool editor = true, params string[] running) =>
        new(running.ToHashSet(), id => id == "a" ? "Alt+Z" : null, paused, editor);

    /// <summary>One line per entry: "A [Alt+Z]", "> Folder", "  B" inside it, "---", "(•)" running, "(x)" disabled, "[v]" checked.</summary>
    private static List<string> Render(IReadOnlyList<TrayEntry> entries, string indent = "")
    {
        var lines = new List<string>();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case TraySeparator:
                    lines.Add(indent + "---");
                    break;
                case TrayFolder folder:
                    lines.Add($"{indent}> {folder.Text}");
                    lines.AddRange(Render(folder.Items, indent + "  "));
                    break;
                case TrayFlowItem item:
                    lines.Add(indent + item.Text + (item.Shortcut is null ? "" : $" [{item.Shortcut}]")
                        + (item.Running ? " (•)" : "") + (item.Enabled ? "" : " (x)"));
                    break;
                case TrayCommandItem command:
                    lines.Add(indent + command.Text + (command.Checked ? " [v]" : "") + (command.Enabled ? "" : " (x)"));
                    break;
            }
        }
        return lines;
    }

    private static readonly string[] Tail = ["Open Daze Control", "Pause shortcuts", "---", "Exit"];

    [Fact]
    public void FlowsInFileOrderThenTheFixedItems()
    {
        var menu = TrayMenu.Build(Snapshot(Flow("a") + Flow("b")), State());

        Assert.Equal(["A [Alt+Z]", "B", "---", .. Tail], Render(menu));
    }

    [Fact]
    public void NoFlowsMeansOnlyTheFixedItems()
    {
        Assert.Equal(Tail, Render(TrayMenu.Build(ConfigSnapshot.Empty, State())));
    }

    [Fact]
    public void FoldersSitAtTheirFirstFlowsPlace()
    {
        var snapshot = Snapshot(Flow("a") + Flow("b", "    tray: { folder: TV }\n") + Flow("c")
            + Flow("d", "    tray: { folder: tv }\n"));

        Assert.Equal(["A [Alt+Z]", "> TV", "  B", "  D", "C", "---", .. Tail], Render(TrayMenu.Build(snapshot, State())));
    }

    [Fact]
    public void ALineAboveAFlowButNeverFirstOrDoubled()
    {
        var snapshot = Snapshot(Flow("a", "    tray: { separator: true }\n") + Flow("b", "    tray: { separator: true }\n")
            + Flow("c", "    tray: { folder: TV, separator: true }\n"));

        Assert.Equal(["A [Alt+Z]", "---", "B", "> TV", "  C", "---", .. Tail], Render(TrayMenu.Build(snapshot, State())));
    }

    [Fact]
    public void HiddenAndTurnedOffFlowsAreLeftOut()
    {
        var snapshot = Snapshot(Flow("a", "    tray: { show: false }\n") + Flow("b", "    enabled: false\n") + Flow("c"));

        Assert.Equal(["C", "---", .. Tail], Render(TrayMenu.Build(snapshot, State())));
    }

    [Fact]
    public void ANeverValidFlowIsGreyedAndSaysItNeedsFixing()
    {
        var snapshot = Snapshot(Flow("a") + "  b:\n    name: B\n    nodes: [{ id: n, step: nope }]\n");

        Assert.Equal(["A [Alt+Z]", "B (needs fixing) (x)", "---", .. Tail], Render(TrayMenu.Build(snapshot, State())));
    }

    [Fact]
    public void AStaleFlowStillShowsAndRuns()
    {
        var good = Snapshot(Flow("b"));
        var stale = ConfigHost.Apply(good, ConfigYaml.Load("flows:\n  b:\n    name: B2\n    nodes: [{ id: n, step: nope }]\n"));

        Assert.Equal(["B", "---", .. Tail], Render(TrayMenu.Build(stale, State())));
    }

    [Fact]
    public void RunningFlowsGetTheBulletAndPauseIsChecked()
    {
        var menu = TrayMenu.Build(Snapshot(Flow("a") + Flow("b")), State(paused: true, running: "b"));

        Assert.Equal(["A [Alt+Z]", "B (•)", "---", "Open Daze Control", "Pause shortcuts [v]", "---", "Exit"], Render(menu));
    }

    [Fact]
    public void OpenIsGreyedWithoutTheEditor()
    {
        Assert.Contains("Open Daze Control (x)", Render(TrayMenu.Build(ConfigSnapshot.Empty, State(editor: false))));
    }

    [Theory]
    [InlineData(new string[0], false, null, "Daze Control")]
    [InlineData(new[] { "Connect Technics" }, true, "X didn't finish", "Daze Control · Running Connect Technics")]
    [InlineData(new string[0], true, "X didn't finish", "Daze Control · Shortcuts paused")]
    [InlineData(new string[0], false, "X didn't finish", "Daze Control · X didn't finish")]
    public void TheTooltipSaysWhatMattersMost(string[] running, bool paused, string? problem, string expected)
    {
        Assert.Equal(expected, TrayMenu.Tooltip(running, paused, problem));
    }

    [Fact]
    public void ALongTooltipIsCut()
    {
        var tooltip = TrayMenu.Tooltip([new string('x', 200)], false, null);

        Assert.Equal(127, tooltip.Length);
        Assert.EndsWith("…", tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FlowOutcome.Succeeded, null)]
    [InlineData(FlowOutcome.Cancelled, null)]
    [InlineData(FlowOutcome.Failed, "TV didn't finish")]
    [InlineData(FlowOutcome.PartiallySucceeded, "TV: done, with a problem")]
    public void OnlyRunsThatWentWrongLeaveAProblem(FlowOutcome outcome, string? expected)
    {
        Assert.Equal(expected, TrayMenu.Problem("TV", outcome));
    }
}
