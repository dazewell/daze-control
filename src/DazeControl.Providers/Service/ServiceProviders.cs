using System.Runtime.InteropServices;
using DazeControl.Runtime.Providers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;

namespace DazeControl.Providers.Service;

public enum ServiceState
{
    Stopped,
    Pending,
    Running,
}

/// <summary>The Service Control Manager behind a seam (D-033).</summary>
public interface IServices
{
    /// <summary>Opens the service with the rights to <paramref name="start"/> (or stop) it and reads its state.</summary>
    /// <exception cref="StepFailedException">No such service, or no rights (the engine never has admin, D-008).</exception>
    ServiceState Open(string name, bool start);

    void RequestStart(string name);

    void RequestStop(string name);

    ServiceState Query(string name);
}

/// <summary>
/// <c>service.start</c> / <c>service.stop</c>: already in that state = success (scd's 1056/1062 idempotency); else
/// ask, then wait until the service gets there. Most services need admin rights to start or stop, which Daze Control
/// never has: those fail at once and say so.
/// </summary>
public abstract class ServiceProvider(IServices services, TimeProvider time, bool start) : IStepProvider
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

    public abstract string Id { get; }

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var name = context.RequiredText("name");
        var target = start ? ServiceState.Running : ServiceState.Stopped;
        if (services.Open(name, start) == target)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (start)
            services.RequestStart(name);
        else
            services.RequestStop(name);
        // The SCM has no awaitable change event short of an APC thread; a run is short, so poll while it lasts.
        while (services.Query(name) != target)
            await Task.Delay(PollEvery, time, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ServiceStartProvider(IServices services, TimeProvider time) : ServiceProvider(services, time, start: true)
{
    public override string Id => "service.start";
}

public sealed class ServiceStopProvider(IServices services, TimeProvider time) : ServiceProvider(services, time, start: false)
{
    public override string Id => "service.stop";
}

/// <summary>The real Service Control Manager; each call opens and closes its own handles.</summary>
public sealed unsafe class WindowsServices : IServices
{
    private const uint ErrorServiceAlreadyRunning = 1056, ErrorServiceNotActive = 1062, ErrorServiceDoesNotExist = 1060, ErrorServiceDisabled = 1058;

    public ServiceState Open(string name, bool start) =>
        With(name, start ? PInvoke.SERVICE_START : PInvoke.SERVICE_STOP, start, State);

    public void RequestStart(string name) => With(name, PInvoke.SERVICE_START, start: true, service =>
    {
        if (!PInvoke.StartService((SC_HANDLE)service.DangerousGetHandle(), 0, null) && Marshal.GetLastPInvokeError() is var error && error != ErrorServiceAlreadyRunning)
            throw Failure(name, error, start: true);
        return true;
    });

    public void RequestStop(string name) => With(name, PInvoke.SERVICE_STOP, start: false, service =>
    {
        if (!PInvoke.ControlService(service, PInvoke.SERVICE_CONTROL_STOP, out _) && Marshal.GetLastPInvokeError() is var error
            && error != ErrorServiceNotActive)
            throw Failure(name, error, start: false);
        return true;
    });

    public ServiceState Query(string name) => With(name, 0, start: true, State);

    private static ServiceState State(SafeHandle service)
    {
        if (!PInvoke.QueryServiceStatus(service, out var status))
            throw new StepFailedException($"Windows couldn't read the service's state (error {Marshal.GetLastPInvokeError()}).");
        return status.dwCurrentState switch
        {
            SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING => ServiceState.Running,
            SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED => ServiceState.Stopped,
            _ => ServiceState.Pending,
        };
    }

    private static T With<T>(string name, uint rights, bool start, Func<SafeHandle, T> action)
    {
        using var manager = PInvoke.OpenSCManager((string?)null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid)
            throw new StepFailedException($"Windows couldn't open the service list (error {Marshal.GetLastPInvokeError()}).");
        using var service = PInvoke.OpenService(manager, name, rights | PInvoke.SERVICE_QUERY_STATUS);
        if (service.IsInvalid)
            throw Failure(name, Marshal.GetLastPInvokeError(), start);
        return action(service);
    }

    internal static StepFailedException Failure(string name, int error, bool start) => error switch
    {
        (int)WIN32_ERROR.ERROR_ACCESS_DENIED => new StepFailedException(
            $"Windows only lets an administrator {(start ? "start" : "stop")} \"{name}\", and {Core.AppInfo.ProductName} runs without admin rights. "
            + "Set the service to start automatically, or change it in Services yourself."),
        (int)ErrorServiceDoesNotExist => new StepFailedException($"There's no service called \"{name}\" (use its service name, as in Services → Properties)."),
        (int)ErrorServiceDisabled => new StepFailedException($"\"{name}\" is disabled in Services."),
        _ => new StepFailedException($"Windows couldn't {(start ? "start" : "stop")} \"{name}\" (error {error})."),
    };
}
