using System.Diagnostics;
using DazeControl.Engine.Diagnostics;

namespace DazeControl.Engine.Lifecycle;

/// <summary>
/// A minute after start and then daily, runs <c>dctl update</c> hidden: it checks GitHub Releases and downloads a
/// newer release, which applies at the next engine start (D-043). In a child process, so the resident engine never
/// loads the HTTP/TLS stack (footprint budget). Only in an install; a failed check waits for the next day.
/// </summary>
internal sealed class UpdateCheck : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(10);

    private readonly string _dctl;
    private readonly ITimer _timer;
    private int _running;

    public UpdateCheck(string dctl, TimeProvider time)
    {
        _dctl = dctl;
        _timer = time.CreateTimer(_ => _ = RunAsync(), null, FirstCheckDelay, CheckInterval);
    }

    private async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            var start = new ProcessStartInfo(_dctl, "update") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start);
            if (process is null)
                return;
            using var timeout = new CancellationTokenSource(MaxRunTime);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                Log.Info("updates: dctl update took over 10 minutes, stopped");
                return;
            }
            var text = $"{await output.ConfigureAwait(false)}{await error.ConfigureAwait(false)}".Trim();
            Log.Info($"updates: dctl update exit {process.ExitCode}: {text}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("updates: dctl update didn't run", e);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
