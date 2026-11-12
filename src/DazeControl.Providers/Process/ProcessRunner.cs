using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Process;

/// <summary>Starts a program and waits for it; shared by <c>process.run</c> and <c>script.powershell</c>.</summary>
internal static class ProcessRunner
{
    /// <summary>Chars of stdout kept for the <c>output</c> value: its last 1,000, so the value's own cut never drops the newest text.</summary>
    internal const int ValueTail = Core.Runs.RunValues.MaxValueLength;

    private const int ErrorTail = 4096; // chars of stderr kept for the failure message
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs to exit; on cancel (timeout, stop) kills the whole tree before rethrowing.</summary>
    public static async Task<(int ExitCode, string LastError)> RunAsync(ProcessStartInfo start, string? stdin, CancellationToken cancellationToken)
    {
        var (exitCode, lastError, _) = await RunAsync(start, stdin, keepOutput: false, cancellationToken).ConfigureAwait(false);
        return (exitCode, lastError);
    }

    /// <summary>As above; with <paramref name="keepOutput"/> also the last 4 KB of stdout (adb answers there).</summary>
    public static async Task<(int ExitCode, string LastError, string Output)> RunAsync(ProcessStartInfo start, string? stdin,
        bool keepOutput, CancellationToken cancellationToken, int outputLimit = ErrorTail)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardInput = stdin is not null;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.StandardOutputEncoding = Encoding.UTF8;

        using var process = Start(start);
        // The parent's exit, not EOF on its pipes: a descendant that inherited them may keep them open for hours.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult();
        if (process.HasExited)
            exited.TrySetResult();

        // Fixed-size chunks, never whole lines: a child writing without newlines can't grow the engine's memory.
        using var drain = new CancellationTokenSource();
        var kept = new StringBuilder();
        var output = keepOutput ? TailAsync(process.StandardOutput, kept, outputLimit, drain.Token) : DiscardAsync(process.StandardOutput.BaseStream, drain.Token);
        var tail = new StringBuilder();
        var error = TailAsync(process.StandardError, tail, ErrorTail, drain.Token);
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await drain.CancelAsync().ConfigureAwait(false);
            throw;
        }
        // The exit can beat the last stderr line: give the readers a bounded moment, then stop them.
        await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false);
        await drain.CancelAsync().ConfigureAwait(false);
        string lastError, text;
        lock (tail)
            lastError = LastLine(tail.ToString());
        lock (kept)
            text = kept.ToString();
        return (process.ExitCode, lastError, text);
    }

    /// <summary>Starts without waiting (shell execute: documents, shortcuts and URLs work too).</summary>
    public static void Launch(ProcessStartInfo start)
    {
        start.UseShellExecute = true;
        using var process = Start(start);
    }

    private static async Task DiscardAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped draining, or the pipe went away with the process.
        }
    }

    private static async Task TailAsync(StreamReader reader, StringBuilder tail, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                lock (tail)
                {
                    tail.Append(buffer, 0, read);
                    if (tail.Length > limit)
                        tail.Remove(0, tail.Length - limit);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped draining, or the pipe went away with the process.
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

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
