using CtrlAltDaze.Editor.ViewModels.Engine;
using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

public sealed class EngineClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _name = "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task WithNoEngineRequestsAnswerNull()
    {
        await using var client = new EngineClient(_name);
        client.Start();

        Assert.Null(await client.SendAsync(new PipeRequest { Op = Ops.List }, Ct));
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task FindsTheEngineGetsEventsAndNoticesItLeave()
    {
        var server = new PipeServer(_name, (r, _) => Task.FromResult(new PipeMessage { Id = r.Id, Ok = true, Error = r.Op }), (_, _) => { });
        server.Start();
        await using var client = new EngineClient(_name);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<PipeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += () => (client.IsRunning ? running : gone).TrySetResult();
        client.EventReceived += e => received.TrySetResult(e);

        client.Start();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(Ops.List, (await client.SendAsync(new PipeRequest { Op = Ops.List }, Ct))?.Error);

        server.Broadcast(new PipeEvent { Kind = EventKinds.ConfigReloaded });
        Assert.Equal(EventKinds.ConfigReloaded, (await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Kind);

        await server.DisposeAsync();
        await gone.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(client.IsRunning);
        Assert.Null(await client.SendAsync(new PipeRequest { Op = Ops.List }, Ct));
    }
}
