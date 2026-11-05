using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Service;

/// <summary>
/// <c>service.running</c>: the service is running (read-only, no admin needed). Starting, stopping or paused is no
/// answer: it throws, so a condition lets the step run and a check fails saying so.
/// </summary>
public sealed class ServiceRunningProbe(IServices services) : IFactProbe
{
    public string Fact => "service.running";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var name = context.RequiredText("name");
        return Task.FromResult(services.Query(name) switch
        {
            ServiceState.Running => new FactReading(true, $"\"{name}\" is running"),
            ServiceState.Stopped => new FactReading(false, $"\"{name}\" is stopped"),
            _ => throw new StepFailedException($"\"{name}\" is starting, stopping or paused."),
        });
    }
}
