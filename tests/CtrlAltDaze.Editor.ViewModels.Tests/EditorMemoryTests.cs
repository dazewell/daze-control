using System.Diagnostics;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>
/// F-036: the editor's view models over the 95-step Probe 100 flow (samples/dev-big.yaml). Copy, paste and undo
/// cycles must not keep memory: what the history holds is bounded and freed with the flow.
/// </summary>
public sealed class EditorMemoryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private ConfigSession Open()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", "dev-big.yaml"), path);
        var session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        session.Load();
        return session;
    }

    private static long Managed()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static void Cycle(FlowCanvasViewModel canvas)
    {
        canvas.SelectAll();
        Assert.True(canvas.Copy());
        canvas.Paste();
        canvas.Undo();
    }

    [Fact]
    public void CopyPasteUndoOfAWholeBigFlowKeepsNoMemory()
    {
        using var session = Open();
        var canvas = new FlowCanvasViewModel(session, "probe-100");
        Assert.True(canvas.Nodes.Count >= 95);
        Cycle(canvas); // warm-up: caches, JIT
        var before = Managed();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
            Cycle(canvas);
        var after = Managed();
        output.WriteLine($"managed: {before / 1048576.0:0.0} MB → {after / 1048576.0:0.0} MB after 5 copy/paste/undo cycles ({clock.ElapsedMilliseconds} ms)");

        canvas.Dispose();
        output.WriteLine($"after closing the flow: {Managed() / 1048576.0:0.0} MB");
        Assert.InRange(after - before, long.MinValue, 4L << 20); // the history holds flow records, not copies of the scene
    }

    [Fact]
    public void AClosedFlowsCanvasAndHistoryAreFreed()
    {
        using var session = Open();
        var canvas = OpenAndEdit(session);

        Managed();
        Assert.False(canvas.IsAlive); // nothing the session keeps (events, clipboard) holds the closed flow's canvas
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndEdit(ConfigSession session)
    {
        var canvas = new FlowCanvasViewModel(session, "probe-100");
        Cycle(canvas);
        canvas.SelectAll();
        canvas.Copy();
        canvas.Paste(); // history and redo both hold entries
        canvas.Dispose();
        return new WeakReference(canvas);
    }

    [Fact]
    public void PillsMeasuredInOnePassReLayOutOnce()
    {
        using var session = Open();
        using var canvas = new FlowCanvasViewModel(session, "probe-100");
        canvas.SelectAll();
        canvas.Copy();
        canvas.Paste();
        var layouts = 0;
        canvas.PropertyChanged += (_, e) => layouts += e.PropertyName == nameof(FlowCanvasViewModel.Arrangement) ? 1 : 0;

        // The control reports every pasted pill's first size from one layout pass (F-036: was one re-layout each).
        canvas.ReportSizes([.. canvas.Nodes.Where(n => n.Kind != CanvasNodeKind.Card).Select(n => (n.Id, 180.0, 52.0))]);
        Assert.Equal(1, layouts);

        canvas.ReportSizes([.. canvas.Nodes.Where(n => n.Kind != CanvasNodeKind.Card).Select(n => (n.Id, 180.0, 52.0))]);
        Assert.Equal(1, layouts); // nothing changed: no re-layout
    }
}
