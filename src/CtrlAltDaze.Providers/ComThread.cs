using System.Runtime.InteropServices;
using CtrlAltDaze.Runtime.Providers;
using Windows.Win32;
using Windows.Win32.System.Com;

namespace CtrlAltDaze.Providers;

/// <summary>
/// Runs COM work on a fresh MTA thread that ends with it: steps are rare, and no COM object or worker outlives the
/// step (idle = zero, D-009). The engine's main thread is STA, so pool threads can't be trusted to be in the MTA.
/// </summary>
internal static unsafe class ComThread
{
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var hr = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            try
            {
                // The token may have fired while the thread started: then no COM side effect at all.
                cancellationToken.ThrowIfCancellationRequested();
                result.TrySetResult(work());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.TrySetCanceled(cancellationToken);
            }
            catch (COMException e)
            {
                result.TrySetException(new StepFailedException($"Windows reported an error (0x{e.HResult:X8}).", e));
            }
            catch (Exception e)
            {
                result.TrySetException(e);
            }
            finally
            {
                if (hr.Succeeded)
                    PInvoke.CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "CtrlAltDaze COM step",
        };
        thread.Start();
        return result.Task;
    }
}
