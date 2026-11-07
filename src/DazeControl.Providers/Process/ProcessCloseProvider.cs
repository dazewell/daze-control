using System.ComponentModel;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Process;

/// <summary>One running program instance, behind a seam so closing is tested without closing anything (D-033).</summary>
public interface ICloseTarget : IDisposable
{
    bool HasExited { get; }

    /// <summary>False when the process belongs to another privilege level (elevated): it can be neither asked nor ended.</summary>
    bool CanControl { get; }

    /// <summary>Asks the main window to close (WM_CLOSE); false when there is no window to ask.</summary>
    bool RequestClose();

    /// <summary>Ends it and the programs it started; true when it was ended, false when it was already gone.</summary>
    bool EndTree();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface ICloseTargets
{
    /// <summary>The running instances with this name (no folder, no ".exe"); the caller disposes them.</summary>
    IReadOnlyList<ICloseTarget> Find(string name);
}

public sealed class WindowsCloseTargets : ICloseTargets
{
    public IReadOnlyList<ICloseTarget> Find(string name)
    {
        var self = Environment.ProcessId;
        var found = new List<ICloseTarget>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
        {
            if (process.Id == self)
                process.Dispose();
            else
                found.Add(new WindowsCloseTarget(process));
        }
        return found;
    }

    private sealed class WindowsCloseTarget(System.Diagnostics.Process process) : ICloseTarget
    {
        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (Win32Exception)
                {
                    return false; // can't even query it: treat as running, CanControl says no
                }
            }
        }

        public bool CanControl
        {
            get
            {
                try
                {
                    // The full-access handle is what both close and end need; an elevated process refuses it.
                    _ = process.Handle;
                    return true;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public bool RequestClose()
        {
            try
            {
                return process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
                return false; // exited meanwhile
            }
        }

        public bool EndTree()
        {
            try
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false; // a child we can't end (elevated); the wait then reports it didn't close
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }
}

/// <summary>
/// <c>process.close</c>: asks the program to close, ends it (and what it started) after <c>force</c>. Not running is
/// success; an elevated program fails with a sentence, never with a prompt (D-008, D-102).
/// </summary>
public sealed class ProcessCloseProvider(ICloseTargets targets, TimeProvider time) : IStepProvider
{
    private static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(5);

    public string Id => "process.close";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var text = context.RequiredText("program");
        if (string.IsNullOrWhiteSpace(text))
            throw new StepFailedException("Choose a program.");
        var name = ProcessRunningProbe.ProgramName(Environment.ExpandEnvironmentVariables(text));
        var force = context.Text("force") is null ? TimeSpan.FromSeconds(10) : context.Duration("force");

        var found = targets.Find(name);
        try
        {
            if (found.Count == 0)
                return;
            if (found.Any(t => !t.HasExited && !t.CanControl))
                throw new StepFailedException($"{name} is running as administrator, so Daze Control can't close it.");
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var target in found)
                target.RequestClose();

            if (await ExitedAsync(found, force, cancellationToken).ConfigureAwait(false))
                return;

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var target in found.Where(t => !t.HasExited))
                target.EndTree();
            if (!await ExitedAsync(found, EndGrace, cancellationToken).ConfigureAwait(false))
                throw new StepFailedException($"{name} didn't close.");
        }
        finally
        {
            foreach (var target in found)
                target.Dispose();
        }
    }

    /// <summary>True when all exited within <paramref name="within"/>; a stop of the step still throws.</summary>
    private async Task<bool> ExitedAsync(IReadOnlyList<ICloseTarget> found, TimeSpan within, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(within, time);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            await Task.WhenAll(found.Select(t => t.WaitForExitAsync(window.Token))).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
