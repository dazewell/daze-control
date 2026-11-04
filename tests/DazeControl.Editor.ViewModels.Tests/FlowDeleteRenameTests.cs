using DazeControl.Core.Model;
using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>A saved rename gives the flow a new id (D-075); Delete with Undo on the Flows list (D-076).</summary>
public sealed class FlowDeleteRenameTests : IDisposable
{
    private const string Yaml = """
        version: 1
        flows:
          new-flow:
            name: New flow
            hotkeys: [Alt+Z]
            nodes:
              - { id: wait, step: wait, with: { duration: 1s } }
          evening:
            name: Evening
            nodes:
              - { id: call, step: flow.run, with: { flow: new-flow } }
          morning:
            name: Morning old
            nodes:
              - { id: wait, step: wait, with: { duration: 1s } }
          tv:
            name: TV
            nodes:
              - { id: wait, step: wait, with: { duration: 1s } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly ConfigSession _session;
    private readonly Dictionary<string, ShortcutInfo> _desktop = [];
    private readonly HashSet<string> _engineFlows = [];

    public FlowDeleteRenameTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(ConfigPath, Yaml);
        _session = new ConfigSession(new ConfigStore(ConfigPath, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        EngineLoads();
        // A tiny engine: it knows the flows it last loaded; create needs one of them.
        _engine.Answer = request => request.Op switch
        {
            Ops.Shortcuts => new PipeMessage { Ok = true, Shortcuts = [.. _desktop.Values] },
            Ops.CreateShortcut when _engineFlows.Contains(request.Flow!) => Put(request.Flow!),
            Ops.CreateShortcut => new PipeMessage { Ok = false, Error = "not found" },
            Ops.RemoveShortcut => new PipeMessage { Ok = _desktop.Remove(request.Flow!) || true },
            _ => new PipeMessage { Ok = true },
        };
    }

    private string ConfigPath => Path.Combine(_folder, "config.yaml");

    public void Dispose()
    {
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private void EngineLoads()
    {
        _engineFlows.Clear();
        _engineFlows.UnionWith(OnDisk().Flows.Select(f => f.Id));
    }

    private PipeMessage Put(string flowId)
    {
        _desktop[flowId] = new ShortcutInfo { FlowId = flowId, Name = flowId, Path = $@"C:\Desktop\{flowId}.lnk", FlowExists = true, CurrentName = true };
        return new PipeMessage { Ok = true, Shortcuts = [_desktop[flowId]] };
    }

    private Configuration OnDisk() => ConfigYaml.Load(File.ReadAllText(ConfigPath)).Config!;

    private void Settle() => _time.Advance(ConfigSession.SaveDelay * 2);

    private static string? Called(Configuration config, string flowId) =>
        ((StepNode)config.Flows.Single(f => f.Id == flowId).FindNode("call")!).With["flow"].ToString();

    private static void Rename(FlowCanvasViewModel canvas, ConfigSession session, string name) =>
        session.UpdateDraft(canvas.FlowId, f => f with { Name = name });

    [Fact]
    public void SavingARenameGivesTheFlowAnIdFromItsNameAndCallersFollow()
    {
        using var canvas = new FlowCanvasViewModel(_session, "new-flow");
        Rename(canvas, _session, "Wake up!");

        Assert.True(canvas.Save());

        var disk = OnDisk();
        Assert.Equal(["wake-up", "evening", "morning", "tv"], disk.Flows.Select(f => f.Id)); // same place
        Assert.Equal("wake-up", Called(disk, "evening"));
        Assert.Equal(["Alt+Z"], disk.Flows[0].Hotkeys); // hotkeys live in the flow: they follow
        Assert.Equal("wake-up", canvas.FlowId);
        Assert.False(canvas.IsMissing);
        Assert.False(canvas.HasChanges);
        Assert.Equal("wake-up", Called(_session.Current, "evening"));
        Assert.Empty(_session.Drafts);
    }

    [Fact]
    public void ARenameToATakenSlugGetsANumber()
    {
        using var canvas = new FlowCanvasViewModel(_session, "new-flow");
        Rename(canvas, _session, "Morning");

        canvas.Save();

        Assert.Equal("morning-2", canvas.FlowId);
        Assert.Equal("morning-2", Called(OnDisk(), "evening"));
    }

    [Fact]
    public void ANameWhoseSlugIsTheIdKeepsIt()
    {
        using var canvas = new FlowCanvasViewModel(_session, "tv");
        var renamed = new List<string>();
        _session.FlowRenamed += (_, n) => renamed.Add(n);
        Rename(canvas, _session, "Tv!");

        canvas.Save();

        Assert.Equal("tv", canvas.FlowId);
        Assert.Empty(renamed);
        Assert.Equal("Tv!", OnDisk().Flows.Single(f => f.Id == "tv").Name);
    }

    [Fact]
    public void OtherEditsDontRenameAHandWrittenId()
    {
        using var canvas = new FlowCanvasViewModel(_session, "morning"); // name "Morning old", id "morning"
        _session.UpdateDraft("morning", f => f with { Popup = false });

        canvas.Save();

        Assert.Equal("morning", canvas.FlowId);
    }

    [Fact]
    public void UndoingTheNameInsideTheDraftLeavesTheId()
    {
        using var canvas = new FlowCanvasViewModel(_session, "new-flow");
        Rename(canvas, _session, "Wake up");
        Rename(canvas, _session, "New flow");

        Assert.False(canvas.HasChanges);
        canvas.Save();

        Assert.Equal("new-flow", canvas.FlowId);
        Assert.Equal("new-flow", Called(OnDisk(), "evening"));
    }

    [Fact]
    public void RenamingBackGivesTheOldIdAgain()
    {
        using var canvas = new FlowCanvasViewModel(_session, "new-flow");
        Rename(canvas, _session, "Wake up");
        canvas.Save();
        Rename(canvas, _session, "New flow");

        canvas.Save();

        Assert.Equal("new-flow", canvas.FlowId);
        Assert.Equal("new-flow", Called(OnDisk(), "evening"));
    }

    [Fact]
    public async Task TheDesktopShortcutMovesOnceTheEngineHasTheNewId()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        Put("new-flow");
        await shell.Desktop.RefreshAsync(TestContext.Current.CancellationToken);
        shell.OpenFlow("new-flow");
        Rename(shell.Editor!, _session, "Wake up");

        shell.Editor!.Save(); // the engine hasn't reloaded yet: create is refused, the old shortcut stays

        Assert.Equal("wake-up", shell.Editor.FlowId);
        Assert.Contains("new-flow", _desktop.Keys);
        Assert.DoesNotContain("wake-up", _desktop.Keys);

        EngineLoads();
        _engine.Raise(new PipeEvent { Kind = EventKinds.ConfigReloaded });
        await Task.Yield();

        Assert.Equal(["wake-up"], _desktop.Keys);
        Assert.NotNull(shell.Desktop.Of("wake-up"));
    }

    [Fact]
    public void DeleteRemovesTheFlowAndUndoPutsItBackInPlace()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        page.Delete("tv");
        Settle();

        Assert.Equal("TV was deleted.", page.DeletedText);
        Assert.True(page.CanUndoDelete);
        Assert.DoesNotContain(OnDisk().Flows, f => f.Id == "tv");
        Assert.DoesNotContain(page.Items, i => i.Id == "tv");

        page.Delete("morning");
        Assert.Equal("Morning old was deleted.", page.DeletedText); // the second Delete ends the first's Undo
        page.UndoDeleteCommand.Execute(null);
        Settle();

        Assert.Null(page.DeletedText);
        Assert.Equal(["new-flow", "evening", "morning"], OnDisk().Flows.Select(f => f.Id));
        Assert.Equal(["new-flow", "evening", "morning"], page.Items.Select(i => i.Id));
    }

    [Fact]
    public void UndoBringsBackTheIdAndHotkey()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        _session.Update(c => c with { Flows = [.. c.Flows.Where(f => f.Id != "evening")] }); // nothing calls new-flow now

        page.Delete("new-flow");
        page.UndoDeleteCommand.Execute(null);
        Settle();

        var flow = OnDisk().Flows[0];
        Assert.Equal("new-flow", flow.Id);
        Assert.Equal(["Alt+Z"], flow.Hotkeys);
    }

    [Fact]
    public void DeleteIsOffWhileAnotherFlowRunsIt()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());
        var item = page.Items.Single(i => i.Id == "new-flow");

        Assert.False(item.CanDelete);
        Assert.Equal("Evening runs this flow. Remove that step first.", item.DeleteOffReason);
        page.Delete("new-flow");
        Assert.Contains(_session.Current.Flows, f => f.Id == "new-flow");

        _session.Update(c => c with { Flows = [.. c.Flows.Select(f => f.Id == "tv" ? f with { Nodes = [new StepNode("call", "flow.run") { With = new([KeyValuePair.Create("flow", (ParamValue)"new-flow")]) }] } : f)] });
        Assert.Equal("Evening and TV run this flow. Remove those steps first.", item.DeleteOffReason);

        _session.Update(c => c with { Flows = [.. c.Flows.Where(f => f.Id is not ("evening" or "tv"))] });
        Assert.True(item.CanDelete);
        Assert.Null(item.DeleteOffReason);
    }

    [Fact]
    public void DeleteIsOffWhenTheFileCantBeEdited()
    {
        File.WriteAllText(ConfigPath, Yaml + "\n  bad: { nodes: 5 }\n"); // mapping errors: read, but not editable
        _session.Load();
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        Assert.False(_session.CanEdit);
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, i => Assert.False(i.CanDelete));
    }

    [Fact]
    public async Task TheShortcutGoesOnlyWhenTheBarEnds()
    {
        Put("tv");
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        await shell.Desktop.RefreshAsync(TestContext.Current.CancellationToken);
        var page = shell.Flows;

        page.Delete("tv");
        Assert.Contains("tv", _desktop.Keys);
        page.UndoDeleteCommand.Execute(null);
        Assert.Contains("tv", _desktop.Keys); // Undo needs no shortcut work

        page.Delete("tv");
        page.NewFlowCommand.Execute(null); // New flow ends the bar
        await Task.Yield();

        Assert.Null(page.DeletedText);
        Assert.DoesNotContain("tv", _desktop.Keys);
    }

    [Fact]
    public async Task AShortcutMadeOutsideTheEditorGoesAndMovesToo()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        Put("tv"); // dctl shortcut tv: the editor's list doesn't know it yet
        Put("morning");
        Assert.Null(shell.Desktop.Of("tv"));

        shell.Flows.Delete("tv");
        await shell.Flows.EndUndoAsync();

        Assert.DoesNotContain("tv", _desktop.Keys);

        shell.OpenFlow("morning");
        _session.UpdateDraft("morning", f => f with { Name = "Sunrise" });
        EngineLoads(); // the engine is quick: create works on the first try
        shell.Editor!.Save();
        _engineFlows.Add("sunrise");
        _engine.Raise(new PipeEvent { Kind = EventKinds.ConfigReloaded });
        await Task.Yield();

        Assert.Contains("sunrise", _desktop.Keys);
        Assert.DoesNotContain("morning", _desktop.Keys);
    }

    [Fact]
    public async Task ARenameWhileTheEngineIsOffMovesTheShortcutWhenItStarts()
    {
        Put("new-flow");
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        _engine.SetRunning(false);
        shell.OpenFlow("new-flow");
        Rename(shell.Editor!, _session, "Wake up");
        shell.Editor!.Save();
        await Task.Yield();
        Assert.Contains("new-flow", _desktop.Keys);

        EngineLoads(); // it starts with the saved file
        _engine.SetRunning(true);
        await Task.Yield();

        Assert.Equal(["wake-up"], _desktop.Keys);
    }

    [Fact]
    public async Task AFailedRemoveKeepsTheMoveForTheNextReload()
    {
        Put("new-flow");
        var removeWorks = false;
        var inner = _engine.Answer!;
        _engine.Answer = r => r.Op == Ops.RemoveShortcut && !removeWorks ? new PipeMessage { Ok = false, Error = "busy" } : inner(r);
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        shell.OpenFlow("new-flow");
        Rename(shell.Editor!, _session, "Wake up");
        EngineLoads();
        _engineFlows.Add("wake-up");
        shell.Editor!.Save();
        await Task.Yield();
        Assert.Contains("new-flow", _desktop.Keys); // the remove failed

        removeWorks = true;
        _engine.Raise(new PipeEvent { Kind = EventKinds.ConfigReloaded });
        await Task.Yield();

        Assert.Equal(["wake-up"], _desktop.Keys);
    }

    [Fact]
    public async Task TwoRenamesBeforeTheEngineReloadsMoveTheShortcutToTheLast()
    {
        Put("new-flow");
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        shell.OpenFlow("new-flow");
        Rename(shell.Editor!, _session, "Wake up");
        shell.Editor!.Save();
        Rename(shell.Editor, _session, "Sunrise");
        shell.Editor.Save();

        EngineLoads();
        _engine.Raise(new PipeEvent { Kind = EventKinds.ConfigReloaded });
        await Task.Yield();

        Assert.Equal(["sunrise"], _desktop.Keys);
    }

    [Fact]
    public void OpeningAFlowEndsTheBar()
    {
        using var shell = new ShellViewModel(_session, _engine, a => a(), _folder);
        shell.Flows.Delete("tv");

        shell.OpenFlow("morning");

        Assert.Null(shell.Flows.DeletedText);
        Assert.False(shell.Flows.CanUndoDelete);
    }

    [Fact]
    public void NewFlowIdsComeFromFlowIds()
    {
        using var page = new FlowsPageViewModel(_session, _engine, a => a());

        page.NewFlowCommand.Execute(null);

        Assert.Equal("new-flow-2", page.Items[^1].Id);
    }
}
