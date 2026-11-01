using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Process;

/// <summary>Starts a program and waits for it; shared by <c>process.run</c> and <c>script.powershell</c>.</summary>
internal static class ProcessRunner
{
    /// <summary>Runs to exit; on cancel (timeout, stop) kills the whole tree before rethrowing.</summary>
    public static async Task<(int ExitCode, string LastError)> RunAsync(ProcessStartInfo start, string? stdin, CancellationToken cancellationToken)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardInput = stdin is not null;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.StandardOutputEncoding = Encoding.UTF8;

        using var process = Start(start);
        var lastError = "";
        var errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                errorClosed.TrySetResult();
            else if (!string.IsNullOrWhiteSpace(e.Data))
                lastError = e.Data.Trim();
        };
        // Output is drained so a chatty child can't block on a full pipe; nothing reads it yet.
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }
        // The exit can beat the last stderr line; give the reader a moment, never block on a grandchild holding it.
        await Task.WhenAny(errorClosed.Task, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
        return (process.ExitCode, lastError);
    }

    /// <summary>Starts without waiting (shell execute: documents, shortcuts and URLs work too).</summary>
    public static void Launch(ProcessStartInfo start)
    {
        start.UseShellExecute = true;
        using var process = Start(start);
    }

    private static System.Diagnostics.Process Start(ProcessStartInfo start)
    {
        try
        {
            return System.Diagnostics.Process.Start(start) ?? throw new StepFailedException($"{start.FileName} didn't start.");
        }
        catch (Win32Exception e)
        {
            throw new StepFailedException($"Couldn't start {start.FileName}: {e.Message}", e);
        }
    }

    private static void KillTree(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Access denied on a child we can't end (elevated); the step fails on its own token anyway.
        }
    }
}
