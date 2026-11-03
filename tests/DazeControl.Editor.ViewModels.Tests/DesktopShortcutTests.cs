using DazeControl.Editor.ViewModels;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>The desktop shortcut row in the flow settings and the Flows card's menu (D-049, UX §5.4).</summary>
public sealed class DesktopShortcutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeEngine _engine = new() { IsRunning = true };
    private readonly ConfigSession _session;
    private readonly Dictionary<string, ShortcutInfo> _desktop = [];

    public DesktopShortcutTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        // A tiny engine: create puts the flow's shortcut under its name, remove takes it away.
        _engine.Answer = request => request.Op switch
        {
            Ops.Shortcuts => new PipeMessage { Ok = true, Shortcuts = [.. _desktop.Values] },
            Ops.CreateShortcut => Put(request.Flow!, "Flow", currentName: true),
            Ops.RemoveShortcut => new PipeMessage { Ok = _desktop.Remove(request.Flow!) || true },
            _ => new PipeMessage { Ok = true },
        };
    }

    public void Dispose()
    {
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private PipeMessage Put(string flowId, string name, bool currentName)
    {
        _desktop[flowId] = new ShortcutInfo { FlowId = flowId, Name = name, Path = $@"C:\Desktop\{name}.lnk", FlowExists = true, CurrentName = currentName };
        return new PipeMessage { Ok = true, Shortcuts = [_desktop[flowId]] };
    }

    private InspectorViewModel Inspector() => new(_session, new FlowCanvasViewModel(_session, "f"), _engine, action => action());

    [Fact]
    public void WithoutTheEngineTheRowSaysToStartIt()
    {
        _engine.IsRunning = false;

        using var inspector = Inspector();

        Assert.Equal("Start Daze Control to add a desktop shortcut.", inspector.DesktopStatus);
        Assert.False(inspector.CanAddToDesktop);
        Assert.True(inspector.ShowAddToDesktop);
    }

    [Fact]
    public async Task AddPutsItOnTheDesktopAndRemoveTakesItAway()
    {
        using var inspector = Inspector();
        Assert.True(inspector.CanAddToDesktop);
        Assert.Equal("Puts a shortcut on your desktop that runs this flow.", inspector.DesktopStatus);

        await inspector.AddToDesktopCommand.ExecuteAsync(null);

        Assert.Contains(_engine.Requests, r => r is { Op: Ops.CreateShortcut, Flow: "f" });
        Assert.True(inspector.HasDesktopShortcut);
        Assert.Equal("On your desktop", inspector.DesktopStatus);
        Assert.True(inspector.CanRemoveFromDesktop);
        Assert.False(inspector.CanUpdateDesktop);

        await inspector.RemoveFromDesktopCommand.ExecuteAsync(null);

        Assert.False(inspector.HasDesktopShortcut);
        Assert.True(inspector.CanAddToDesktop);
    }

    [Fact]
    public async Task AnOldNameOffersUpdate()
    {
        Put("f", "Old name", currentName: false);
        using var inspector = Inspector();
        await Task.Yield();

        Assert.True(inspector.CanUpdateDesktop);
        Assert.Equal("On your desktop as “Old name”", inspector.DesktopStatus);

        await inspector.AddToDesktopCommand.ExecuteAsync(null);

        Assert.False(inspector.CanUpdateDesktop);
    }

    [Fact]
    public async Task AnEngineErrorIsShown()
    {
        using var inspector = Inspector();
        _engine.Answer = request => request.Op == Ops.CreateShortcut
            ? PipeMessage.Fail(request.Id, "There's no flow called \"f\".")
            : new PipeMessage { Ok = true };

        await inspector.AddToDesktopCommand.ExecuteAsync(null);

        Assert.Equal("There's no flow called \"f\".", inspector.DesktopError);
    }

    [Fact]
    public async Task TheEngineComingBackRefreshesTheRow()
    {
        _engine.IsRunning = false;
        using var inspector = Inspector();
        Put("f", "Flow", currentName: true);

        _engine.SetRunning(true);
        await Task.Yield();

        Assert.True(inspector.HasDesktopShortcut);
    }

    [Fact]
    public async Task TheCardMenuAddsAndRemovesAndSharesTheStoreWithTheSettings()
    {
        var store = new DesktopShortcutStore(_engine);
        using var page = new FlowsPageViewModel(_session, _engine, action => action(), store);
        using var inspector = new InspectorViewModel(_session, new FlowCanvasViewModel(_session, "f"), _engine, action => action(), desktop: store);
        var card = page.Items.Single(i => i.Id == "f");
        Assert.Equal("Add to desktop", card.DesktopMenuText);

        await page.ToggleDesktopAsync("f");

        Assert.True(card.HasDesktopShortcut);
        Assert.Equal("Remove from desktop", card.DesktopMenuText);
        Assert.True(inspector.HasDesktopShortcut);

        await page.ToggleDesktopAsync("f");

        Assert.False(card.HasDesktopShortcut);
        Assert.False(inspector.HasDesktopShortcut);
    }

    [Fact]
    public void TheCardMenuIsOffWithoutTheEngine()
    {
        _engine.IsRunning = false;

        using var page = new FlowsPageViewModel(_session, _engine, action => action());

        var card = page.Items.Single(i => i.Id == "f");
        Assert.False(card.CanChangeDesktop);
        Assert.Equal("Start Daze Control to add a desktop shortcut.", card.DesktopMenuText);
    }
}
