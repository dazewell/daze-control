using System.Security.Principal;
using DazeControl.Providers.Service;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class ServiceProviderTests
{
    /// <summary>Reports <see cref="States"/> in turn on each query (the last repeats); records the requests.</summary>
    private sealed class FakeServices(ServiceState initial, params ServiceState[] states) : IServices
    {
        private int _queries;

        public List<string> Calls { get; } = [];

        public ServiceState Open(string name, bool start) => initial;

        public void RequestStart(string name) => Calls.Add("start " + name);

        public void RequestStop(string name) => Calls.Add("stop " + name);

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
    public async Task StartWithoutAdminFailsClearlyAndChangesNothing()
    {
        // EventLog is always running. Without admin, opening it with start rights is refused before anything is sent;
        // with admin (an elevated test run) the step sees it running and does nothing. No state changes either way.
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var step = new ServiceStartProvider(new WindowsServices(), TimeProvider.System).RunAsync(("name", "EventLog"));
        if (elevated)
        {
            await step;
            return;
        }
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => step);
        Assert.Equal("Windows only lets an administrator start \"EventLog\", and Daze Control runs without admin rights. "
            + "Set the service to start automatically, or change it in Services yourself.", failure.Message);
    }

    [Fact]
    public void RealStateIsReadable() =>
        Assert.Equal(ServiceState.Running, new WindowsServices().Query("EventLog"));
}
