using System.Security.Principal;
using CtrlAltDaze.Providers.Service;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

public class ServiceProviderTests
{
    /// <summary>Answers each query with the next state (the last repeats); records the requests.</summary>
    private sealed class FakeServices(params ServiceState[] states) : IServices
    {
        private int _queries;

        public List<string> Calls { get; } = [];

        public void RequestStart(string name, CancellationToken cancellationToken) => Calls.Add("start " + name);

        public void RequestStop(string name, CancellationToken cancellationToken) => Calls.Add("stop " + name);

        public ServiceState Query(string name) => states[Math.Min(_queries++, states.Length - 1)];
    }

    [Fact]
    public async Task StartWaitsUntilRunning()
    {
        var services = new FakeServices(ServiceState.Stopped, ServiceState.Pending, ServiceState.Running);
        await new ServiceStartProvider(services, TimeProvider.System).RunAsync(("name", "SunshineService"));
        Assert.Equal(["start SunshineService"], services.Calls);
    }

    [Fact]
    public async Task StopAlreadyUnderWayIsWaitedOutNotSentAgain()
    {
        var services = new FakeServices(ServiceState.Pending, ServiceState.Pending, ServiceState.Stopped);
        await new ServiceStopProvider(services, TimeProvider.System).RunAsync(("name", "x"));
        Assert.Empty(services.Calls);
    }

    [Fact]
    public async Task PausedServiceIsNotStartedBlindly()
    {
        var services = new FakeServices(ServiceState.Paused);
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => new ServiceStartProvider(services, TimeProvider.System).RunAsync(("name", "x")));
        Assert.Equal("\"x\" is paused; resume it in Services.", failure.Message);
        Assert.Empty(services.Calls);
    }

    [Fact]
    public async Task AlreadyInThatStateIsSuccessWithoutARequest()
    {
        var running = new FakeServices(ServiceState.Running);
        await new ServiceStartProvider(running, TimeProvider.System).RunAsync(("name", "x"));
        var stopped = new FakeServices(ServiceState.Stopped);
        await new ServiceStopProvider(stopped, TimeProvider.System).RunAsync(("name", "x"));
        Assert.Empty(running.Calls);
        Assert.Empty(stopped.Calls);
    }

    [Fact]
    public async Task StopThatNeverStopsEndsWithTheStepTimeout()
    {
        var services = new FakeServices(ServiceState.Running, ServiceState.Pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ServiceStopProvider(services, TimeProvider.System)
            .ExecuteAsync(Steps.Context("service.stop", ("name", "x")), timeout.Token));
        Assert.Equal(["stop x"], services.Calls);
    }

    [Fact]
    public async Task UnknownServiceFailsPlainly()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => new ServiceStartProvider(new WindowsServices(), TimeProvider.System).RunAsync(("name", "NoSuchService-" + Guid.NewGuid().ToString("N"))));
        Assert.StartsWith("There's no service called", failure.Message);
    }

    [Fact]
    public async Task StartingARunningProtectedServiceNeedsNoRights() =>
        // Read-only query first: EventLog is running, so nothing is asked of the SCM that a user can't do.
        await new ServiceStartProvider(new WindowsServices(), TimeProvider.System).RunAsync(("name", "EventLog"));

    [Fact]
    public void StartRequestWithoutAdminFailsClearlyAndChangesNothing()
    {
        // EventLog is always running. Without admin, opening it with start rights is refused before anything is sent;
        // with admin (an elevated test run) StartService answers "already running", which counts as done.
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            new WindowsServices().RequestStart("EventLog", TestContext.Current.CancellationToken);
            return;
        }
        var failure = Assert.Throws<StepFailedException>(() => new WindowsServices().RequestStart("EventLog", TestContext.Current.CancellationToken));
        Assert.Equal("Windows only lets an administrator start \"EventLog\", and Ctrl Alt Daze runs without admin rights. "
            + "Set the service to start automatically, or change it in Services yourself.", failure.Message);
        Assert.Equal(ServiceState.Running, new WindowsServices().Query("EventLog"));
    }

    [Fact]
    public void RealStateIsReadable() =>
        Assert.Equal(ServiceState.Running, new WindowsServices().Query("EventLog"));

    [Fact]
    public void CancelledRequestSendsNothing()
    {
        // The token is checked after the handles open; a cancelled request never reaches StartService. With
        // query-only rights a user can't open EventLog for start, so this only runs elevated.
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return;
        Assert.Throws<OperationCanceledException>(() => new WindowsServices().RequestStart("EventLog", new CancellationToken(canceled: true)));
    }
}
