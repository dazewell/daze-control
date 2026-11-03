using DazeControl.Core.Model;
using DazeControl.Core.Yaml;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Save / Cancel in the flow editor (D-047): flow edits stay a draft until Save.</summary>
public sealed class FlowDraftTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;

    public FlowDraftTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(_path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(_path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // backups of a read-only file are read-only too
        Directory.Delete(_folder, recursive: true);
    }

    private Flow OnDisk() => ConfigYaml.Load(File.ReadAllText(_path)).Config!.Flows.Single(f => f.Id == "f");

    private void Settle() => _time.Advance(ConfigSession.SaveDelay * 2);

    [Fact]
    public void CanvasEditsStayADraftUntilSave()
    {
        var before = OnDisk();
        _canvas.AddStep("wait");
        Settle();

        Assert.True(_canvas.HasChanges);
        Assert.True(_session.HasUnsavedChanges);
        Assert.Equal(before.Nodes.Count, OnDisk().Nodes.Count);
        Assert.Equal(before.Nodes.Count + 1, _canvas.Flow!.Nodes.Count);

        _canvas.Save();

        Assert.False(_canvas.HasChanges);
        Assert.False(_session.HasUnsavedChanges);
        Assert.Equal(before.Nodes.Count + 1, OnDisk().Nodes.Count);
        Assert.False(_canvas.CanUndo);
    }

    [Fact]
    public void DiscardGoesBackToTheSavedFlow()
    {
        var saved = _canvas.Flow;
        _canvas.Select("bt");
        _canvas.DeleteSelection();
        _canvas.AddStep("notify");

        _canvas.Discard();

        Assert.Equal(saved, _canvas.Flow);
        Assert.False(_canvas.HasChanges);
        Assert.False(_canvas.CanUndo);
        Assert.NotNull(_canvas.Find("bt"));
    }

    [Fact]
    public void UndoStaysInsideTheDraftAndBackToSavedIsClean()
    {
        _canvas.AddStep("wait");
        Assert.True(_canvas.HasChanges);
        _canvas.Undo();
        Assert.False(_canvas.HasChanges);
        Assert.False(_canvas.CanUndo);
    }

    [Fact]
    public void LibraryEditsStillAutosaveWhileAFlowHasADraft()
    {
        _canvas.AddStep("wait");
        _session.Update(c => c with { Library = c.Library with { Audio = [.. c.Library.Audio, new AudioDevice("hdmi", "HDMI")] } });
        Settle();

        var disk = ConfigYaml.Load(File.ReadAllText(_path)).Config!;
        Assert.Contains(disk.Library.Audio, d => d.Key == "hdmi");
        Assert.Equal(7, disk.Flows.Single(f => f.Id == "f").Nodes.Count); // the draft's new step isn't written
        Assert.True(_canvas.HasChanges);
        Assert.Contains(_session.Current.Library.Audio, d => d.Key == "hdmi");
    }

    [Fact]
    public void ALibraryRenameReachesTheDraftAndTheSavedFlow()
    {
        _canvas.AddStep("wait");
        _session.Update(c => LibraryEdits.Rename(c, LibraryKind.Bluetooth, "technics", "buds").Config);
        Settle();

        Assert.Equal("buds", ((ScalarParam)((StepNode)OnDisk().FindNode("bt")!).With["device"]).Text);
        Assert.Equal("buds", ((ScalarParam)((StepNode)_canvas.Flow!.FindNode("bt")!).With["device"]).Text);
        Assert.Equal(8, _canvas.Flow.Nodes.Count);
    }

    [Fact]
    public void TheOnSwitchOnTheFlowsListSavesAtOnceAndKeepsTheDraft()
    {
        using var flows = new FlowsPageViewModel(_session, new FakeEngine(), action => action());
        _canvas.AddStep("wait");
        flows.SetEnabled("f", false);
        Settle();

        Assert.False(OnDisk().Enabled);
        Assert.Equal(7, OnDisk().Nodes.Count);
        Assert.False(_canvas.Flow!.Enabled);
        Assert.True(_canvas.HasChanges);
    }

    [Fact]
    public void AReloadFromDiskKeepsTheDraft()
    {
        _canvas.AddStep("wait");
        var text = File.ReadAllText(_path).Replace("Connect Technics", "Connect buds", StringComparison.Ordinal);
        File.WriteAllText(_path, text);
        _session.CheckOutsideChange();

        Assert.Equal(OutsideChange.Reloaded, _session.OutsideChange);
        Assert.True(_canvas.HasChanges);
        Assert.Equal(8, _canvas.Flow!.Nodes.Count);
    }

    [Fact]
    public void AFailedSaveKeepsTheDraftItsUndoAndTheSavedVersion()
    {
        var before = OnDisk();
        _canvas.AddStep("wait");
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(_canvas.Save());

            Assert.True(_session.HasSaveError);
            Assert.True(_canvas.HasChanges);
            Assert.True(_canvas.CanUndo);
            Assert.Equal(before, _session.SavedFlow("f"));

            _canvas.Discard(); // Cancel still goes back to what the file has
            Assert.Equal(before.Nodes.Count, _canvas.Flow!.Nodes.Count);
        }
        finally
        {
            File.SetAttributes(_path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ASaveRetriedAfterAFailureWrites()
    {
        _canvas.AddStep("wait");
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        Assert.False(_canvas.Save());
        File.SetAttributes(_path, FileAttributes.Normal);

        Assert.True(_canvas.Save());

        Assert.False(_canvas.HasChanges);
        Assert.Equal(8, OnDisk().Nodes.Count);
    }

    [Fact]
    public void AnUnreadableReloadKeepsTheDraft()
    {
        _canvas.AddStep("wait");
        File.WriteAllText(_path, "flows: [unclosed");
        _session.CheckOutsideChange();

        Assert.True(_session.HasLoadError);
        Assert.True(_canvas.HasChanges);
        Assert.True(_session.HasUnsavedChanges);
        Assert.Equal(8, _canvas.Flow!.Nodes.Count);
    }
}
