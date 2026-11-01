using System.IO.Pipes;
using System.Text;
using DazeControl.Ipc;

namespace DazeControl.Runtime.Tests;

public sealed class PipeTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _name = "DazeControl.Tests-" + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PipeServer _server;
    private readonly List<string> _errors = [];

    public PipeTests()
    {
        _server = new PipeServer(_name, async (request, token) =>
        {
            if (request.Op == "slow")
                await _gate.Task.WaitAsync(token);
            if (request.Op == "throw")
                throw new InvalidOperationException("boom");
            return new PipeMessage { Id = request.Id, Ok = true, Error = request.Op + ":" + request.Flow };
        }, (what, _) => { lock (_errors) _errors.Add(what); });
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _gate.TrySetResult();
        await _server.DisposeAsync();
    }

    private async Task<PipeClient> Connect() =>
        await PipeClient.ConnectAsync(_name, TimeSpan.FromSeconds(5), Ct) ?? throw new InvalidOperationException("no server");

    [Fact]
    public async Task ARequestGetsItsAnswer()
    {
        await using var client = await Connect();

        var answer = await client.SendAsync(new PipeRequest { Op = "list", Flow = "tv" }, Ct);

        Assert.True(answer.Ok);
        Assert.Equal("list:tv", answer.Error);
    }

    [Fact]
    public async Task ASlowRequestDoesNotBlockAnotherClient()
    {
        await using var slow = await Connect();
        await using var fast = await Connect();
        var pending = slow.SendAsync(new PipeRequest { Op = "slow" }, Ct);

        Assert.True((await fast.SendAsync(new PipeRequest { Op = "status" }, Ct)).Ok);
        Assert.False(pending.IsCompleted);
        _gate.SetResult();
        Assert.True((await pending).Ok);
    }

    [Fact]
    public async Task SubscribersGetEventsOthersDont()
    {
        await using var subscriber = await Connect();
        await using var other = await Connect();
        Assert.True((await subscriber.SendAsync(new PipeRequest { Op = Ops.Subscribe }, Ct)).Ok);

        _server.Broadcast(new PipeEvent { Kind = EventKinds.ConfigReloaded });

        var message = await subscriber.ReadAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(EventKinds.ConfigReloaded, message!.Event!.Kind);
        Assert.Equal("status:", (await other.SendAsync(new PipeRequest { Op = "status" }, Ct)).Error);
    }

    [Fact]
    public async Task AHandlerThatThrowsAnswersWithAnErrorAndTheServerLives()
    {
        await using var client = await Connect();

        var answer = await client.SendAsync(new PipeRequest { Op = "throw" }, Ct);

        Assert.False(answer.Ok);
        Assert.Contains("boom", answer.Error, StringComparison.Ordinal);
        Assert.True((await client.SendAsync(new PipeRequest { Op = "list" }, Ct)).Ok);
        Assert.Equal(["pipe: throw failed"], _errors);
    }

    [Theory]
    [InlineData("{\"protocol\":99,\"id\":3,\"op\":\"list\"}", 3, "protocol 1, not 99")]
    [InlineData("not json", 0, "Not a valid request")]
    public async Task BadLinesGetAnError(string line, int id, string error)
    {
        await using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, Ct);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), Ct);
        using var reader = new StreamReader(pipe);

        var answer = System.Text.Json.JsonSerializer.Deserialize(await reader.ReadLineAsync(Ct) ?? "", IpcJson.Default.PipeMessage)!;

        Assert.False(answer.Ok);
        Assert.Equal(id, answer.Id);
        Assert.Contains(error, answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoServerMeansNoClient()
    {
        Assert.Null(await PipeClient.ConnectAsync("DazeControl.Tests-none", TimeSpan.FromMilliseconds(200), Ct));
    }

    [Fact]
    public async Task DisposeEndsOpenConnections()
    {
        var client = await Connect();
        var pending = client.SendAsync(new PipeRequest { Op = "slow" }, Ct);

        await _server.DisposeAsync();

        await Assert.ThrowsAnyAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await client.DisposeAsync();
    }
}
