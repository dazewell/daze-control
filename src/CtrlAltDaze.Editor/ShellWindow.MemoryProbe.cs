using System.Diagnostics;
using System.Globalization;

namespace CtrlAltDaze.Editor;

/// <summary>
/// <c>--memory-probe &lt;flow id&gt; &lt;log&gt;</c> (with <c>--config</c>, F-036): opens the flow, runs select all, copy,
/// paste and undo a few times through the view model, closes the flow, opens and closes it three more times, closes
/// the window, and logs the process's memory after each step. <c>scripts/editor-memory.ps1</c> runs it on the dev-big sample's Probe 100.
/// </summary>
public sealed partial class ShellWindow
{
    private async Task RunMemoryProbeAsync(string flowId, string logPath)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Note(string what)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var me = Process.GetCurrentProcess();
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{what,-24} private {me.PrivateMemorySize64 / 1048576.0,7:0.0} MB  working set {me.WorkingSet64 / 1048576.0,7:0.0} MB  managed {GC.GetTotalMemory(false) / 1048576.0,6:0.0} MB"));
        }
        // Long enough for layout, measuring, the glide and the deferred refits to finish.
        static Task Settle() => Task.Delay(TimeSpan.FromSeconds(4));

        try
        {
            await Settle();
            Note("flows page");
            ViewModel.OpenFlow(flowId);
            UpdatePages();
            await Settle();
            Note("flow open");
            for (var i = 1; i <= 3; i++)
            {
                if (ViewModel.Editor is not { } editor)
                    throw new InvalidOperationException("The flow didn't open: " + flowId);
                editor.SelectAll();
                editor.Copy();
                editor.Paste();
                await Settle();
                Note($"paste {i} ({editor.Nodes.Count} boxes)");
                editor.Undo();
                await Settle();
                Note($"undo {i} ({editor.Nodes.Count} boxes)");
            }
            ViewModel.CloseFlow();
            UpdatePages();
            await Settle();
            Note("flow closed");
            // Opening and closing again must come back to the same level: views and history go with the flow.
            for (var i = 1; i <= 3; i++)
            {
                ViewModel.OpenFlow(flowId);
                UpdatePages();
                await Settle();
                ViewModel.CloseFlow();
                UpdatePages();
                await Settle();
                Note($"open and close {i}");
            }
            log.WriteLine("done"); // the script checks for it

        }
        catch (Exception ex)
        {
            log.WriteLine("failed: " + ex);
            Environment.ExitCode = 1; // the script fails too
        }
        Close();
    }
}
