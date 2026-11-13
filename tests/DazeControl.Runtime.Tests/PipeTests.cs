using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DazeControl.Ipc;

namespace DazeControl.Runtime.Tests;

public sealed class PipeTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _name = "DazeControl.Tests-" + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PipeServer _server;
    private readonly List<string> _errors = [];

    public PipeTests()
    {
        _server = new PipeServer(_name, async (request, token) =>
        {
            if (request.Op == "slow")
            {
                _slowStarted.TrySetResult();
                await _gate.Task.WaitAsync(token);
            }
            if (request.Op == "echo")
                return new PipeMessage { Id = request.Id, Ok = true, Error = JsonSerializer.Serialize(request, IpcJson.Default.PipeRequest) };
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
    public async Task EveryRequestFieldReachesTheServer()
    {
        // The client copies the request to stamp its id: a field added to PipeRequest but not to that copy is lost.
        var sent = new PipeRequest();
        foreach (var property in typeof(PipeRequest).GetProperties().Where(p => p.Name is not ("Protocol" or "Id")))
        {
            object value = property.PropertyType switch
            {
                var t when t == typeof(string) => property.Name == "Op" ? "echo" : property.Name,
                var t when t == typeof(bool) || t == typeof(bool?) => true,
                var t when t == typeof(int?) => 3,
                var t when t == typeof(Dictionary<string, string>) => new Dictionary<string, string> { ["audio"] = "sony", ["text"] = "a=b" },
                var t => throw new InvalidOperationException($"Give {property.Name} ({t}) a test value."),
            };
            property.SetValue(sent, value);
        }
        await using var client = await Connect();

        var echoed = JsonSerializer.Deserialize((await client.SendAsync(sent, Ct)).Error!, IpcJson.Default.PipeRequest)!;

        foreach (var property in typeof(PipeRequest).GetProperties().Where(p => p.Name is not ("Protocol" or "Id")))
            Assert.Equal(property.GetValue(sent), property.GetValue(echoed));
    }

    [Fact]
    public async Task OnlyARequestWithInputsAsksForTheNewerProtocol()
    {
        // An older engine would ignore Inputs and run on defaults: those requests say version 2, so it refuses them (T24.9).
        await using var client = await Connect();

        async Task<int> Stamped(PipeRequest request) =>
            JsonSerializer.Deserialize((await client.SendAsync(request, Ct)).Error!, IpcJson.Default.PipeRequest)!.Protocol;

        Assert.Equal(Protocol.Version, await Stamped(new PipeRequest { Op = "echo" }));
        Assert.Equal(Protocol.Version, await Stamped(new PipeRequest { Op = "echo", Inputs = [] }));
        Assert.Equal(Protocol.WithInputs, await Stamped(new PipeRequest { Op = "echo", Inputs = new() { ["a"] = "b" } }));
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
    [InlineData("{\"protocol\":99,\"id\":3,\"op\":\"list\"}", 3, "protocol 2, not 99")]
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

    [Fact]
    public async Task EventsDuringSendAreKeptForReadAsync()
    {
        await using var subscriber = await Connect();
        Assert.True((await subscriber.SendAsync(new PipeRequest { Op = Ops.Subscribe }, Ct)).Ok);
        var pending = subscriber.SendAsync(new PipeRequest { Op = "slow" }, Ct);
        await WaitUntil(() => _slowStarted.Task.IsCompleted);

        _server.Broadcast(new PipeEvent { Kind = EventKinds.ConfigReloaded });
        _gate.SetResult();
        Assert.True((await pending).Ok);

        var message = await subscriber.ReadAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(EventKinds.ConfigReloaded, message!.Event!.Kind);
    }

    [Fact]
    public async Task ASubscriberThatNeverReadsIsDropped()
    {
        await using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, Ct);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"protocol\":1,\"id\":1,\"op\":\"subscribe\"}\n"), Ct);
        await pipe.FlushAsync(Ct);
        await Task.Delay(200, Ct); // let the subscribe land

        var big = new string('x', 4096);
        for (var i = 0; i < PipeServer.QueueLimit * 8 && !HasError("pipe: client too slow, dropped"); i++)
        {
            _server.Broadcast(new PipeEvent { Kind = EventKinds.ConfigReloaded, FlowId = big });
            await Task.Yield();
        }

        Assert.True(HasError("pipe: client too slow, dropped"));
        await using (var other = await Connect())
            Assert.True((await other.SendAsync(new PipeRequest { Op = "status" }, Ct)).Ok);
        // Its cut-off writes (the unread subscribe answer among them) don't hold up the exit.
        await _server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task StopAcceptingStillWritesPendingAnswers()
    {
        await using var client = await Connect();
        var pending = client.SendAsync(new PipeRequest { Op = "slow" }, Ct);
        await WaitUntil(() => _slowStarted.Task.IsCompleted);

        await _server.StopAcceptingAsync();
        Assert.Null(await PipeClient.ConnectAsync(_name, TimeSpan.FromMilliseconds(200), Ct));
        var draining = _server.DrainAsync(Ct);
        _gate.SetResult();

        Assert.Equal("slow:", (await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Error);
        await draining.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    private bool HasError(string what)
    {
        lock (_errors)
            return _errors.Contains(what);
    }

    [Fact]
    public async Task ARequestLineOverTheLimitDropsTheClient()
    {
        await using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var chunk = Encoding.UTF8.GetBytes(new string('x', 4096));
        var sending = Task.Run(async () =>
        {
            try
            {
                for (var sent = 0; sent <= PipeServer.MaxLineBytes * 2; sent += chunk.Length)
                    await pipe.WriteAsync(chunk, stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // Dropped.
            }
        }, Ct);

        await WaitUntil(() => HasError("pipe: request too long, client dropped"));
        await stop.CancelAsync();
        await sending;
        await using var other = await Connect();
        Assert.True((await other.SendAsync(new PipeRequest { Op = "status" }, Ct)).Ok);
    }

    [Fact]
    public async Task RequestsBeyondThePendingLimitAreRefused()
    {
        await using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, Ct);
        var lines = string.Concat(Enumerable.Range(1, PipeServer.MaxPendingRequests + 1)
            .Select(i => $"{{\"protocol\":1,\"id\":{i},\"op\":\"slow\"}}\n"));
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(lines), Ct);
        using var reader = new StreamReader(pipe);

        var answer = System.Text.Json.JsonSerializer.Deserialize(await reader.ReadLineAsync(Ct) ?? "", IpcJson.Default.PipeMessage)!;

        Assert.False(answer.Ok);
        Assert.Equal(PipeServer.MaxPendingRequests + 1, answer.Id);
        Assert.Contains("Too many requests", answer.Error, StringComparison.Ordinal);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10, Ct);
        Assert.True(condition());
    }
}
