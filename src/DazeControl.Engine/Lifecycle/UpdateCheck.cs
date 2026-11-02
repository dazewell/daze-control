using System.Diagnostics;
using DazeControl.Engine.Diagnostics;

namespace DazeControl.Engine.Lifecycle;

/// <summary>
/// A minute after start and then daily, runs <c>dctl update</c> hidden: it checks GitHub Releases and downloads a
/// newer release, which applies at the next engine start (D-043). In a child process, so the resident engine never
/// loads the HTTP/TLS stack (footprint budget). Only in an install; a failed check waits for the next day. Disposing
/// stops a check that is running (the child is killed: a half download is redone next time).
/// </summary>
internal sealed class UpdateCheck : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);

    private readonly string _dctl;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task _current = Task.CompletedTask;
    private bool _disposed;

    public UpdateCheck(string dctl, TimeProvider time)
    {
        _dctl = dctl;
        _timer = time.CreateTimer(_ => Start(), null, FirstCheckDelay, CheckInterval);
    }

    private void Start()
    {
        lock (_gate)
        {
            // One check at a time; none once disposed (a timer callback can still be queued then).
            if (_disposed || !_current.IsCompleted)
                return;
            _current = RunAsync(_stop.Token);
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            var start = new ProcessStartInfo(_dctl, "update")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(MaxRunTime);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Log.Info(stop.IsCancellationRequested ? "updates: engine exiting, dctl update stopped" : "updates: dctl update took over 10 minutes, stopped");
                return;
            }
            var text = $"{await output.ConfigureAwait(false)}{await error.ConfigureAwait(false)}".Trim();
            Log.Info($"updates: dctl update exit {process.ExitCode}: {text}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("updates: dctl update didn't run", e);
        }
    }

    public void Dispose()
    {
        Task current;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            current = _current;
        }
        _timer.Dispose();
        _stop.Cancel();
        // RunAsync logs its own failures and never throws; wait only briefly so exit stays within its grace.
        current.Wait(ExitWait);
        // _stop stays undisposed: a check still running after the wait may yet read its (cancelled) token.
    }
}
