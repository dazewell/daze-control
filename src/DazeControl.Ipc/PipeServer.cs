using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DazeControl.Ipc;

/// <summary>
/// The engine's end of the pipe (D-011): current user only, any number of clients, requests on one connection
/// handled concurrently (a <c>run --wait</c> doesn't block a <c>status</c>), answers and events written one line at a
/// time. Idle cost: one pending overlapped connect, no thread. Never throws to its caller; failures go to
/// <c>onError</c>.
/// </summary>
/// <param name="pipeName"><see cref="Protocol.CurrentPipeName"/> in the engine.</param>
/// <param name="handler">Answers one request; may run long (run with wait). Gets the request's connection token.</param>
/// <param name="onError">For the log.</param>
public sealed class PipeServer(string pipeName, Func<PipeRequest, CancellationToken, Task<PipeMessage>> handler,
    Action<string, Exception>? onError = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly HashSet<Connection> _connections = [];
    private Task? _accepting;
    private int _disposed;

    public void Start() => _accepting = AcceptAsync();

    /// <summary>Sends an event to every subscribed client; a slow or gone client never blocks the caller.</summary>
    public void Broadcast(PipeEvent e)
    {
        Connection[] subscribers;
        lock (_lock)
            subscribers = [.. _connections.Where(c => c.Subscribed)];
        if (subscribers.Length == 0)
            return;
        var line = JsonSerializer.Serialize(new PipeMessage { Ok = true, Event = e }, IpcJson.Default.PipeMessage);
        foreach (var connection in subscribers)
            _ = connection.WriteAsync(line);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException e)
            {
                // Another engine owns the name (the mutex says otherwise, so a race on exit): give up quietly.
                onError?.Invoke("pipe: can't create", e);
                return;
            }
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException e)
            {
                onError?.Invoke("pipe: connect", e);
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            var connection = new Connection(this, pipe);
            lock (_lock)
                _connections.Add(connection);
            connection.Run = ServeAsync(connection);
        }
    }

    private async Task ServeAsync(Connection connection)
    {
        var pending = new List<Task>();
        try
        {
            using var reader = new StreamReader(connection.Pipe, new UTF8Encoding(false), leaveOpen: true);
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(AnswerAsync(connection, line));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Client gone or engine stopping.
        }
        finally
        {
            await connection.Gone.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);
            lock (_lock)
                _connections.Remove(connection);
            await connection.Pipe.DisposeAsync().ConfigureAwait(false);
            connection.Dispose();
        }
    }

    private async Task AnswerAsync(Connection connection, string line)
    {
        PipeMessage answer;
        PipeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize(line, IpcJson.Default.PipeRequest);
            if (request is null)
                answer = PipeMessage.Fail(0, "Empty request.");
            else if (request.Protocol != Protocol.Version)
                answer = PipeMessage.Fail(request.Id, $"The engine speaks protocol {Protocol.Version}, not {request.Protocol}. Update Daze Control.");
            else if (request.Op == Ops.Subscribe)
            {
                connection.Subscribed = true;
                answer = new PipeMessage { Id = request.Id, Ok = true };
            }
            else
                answer = await handler(request, connection.Gone.Token).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            answer = PipeMessage.Fail(0, $"Not a valid request: {e.Message}");
        }
        catch (OperationCanceledException)
        {
            return; // client gone
        }
#pragma warning disable CA1031 // fail-soft: a handler bug answers this request with an error, the engine lives
        catch (Exception e)
#pragma warning restore CA1031
        {
            onError?.Invoke($"pipe: {request?.Op} failed", e);
            answer = PipeMessage.Fail(request?.Id ?? 0, $"The engine hit a problem: {e.Message}");
        }
        await connection.WriteAsync(JsonSerializer.Serialize(answer, IpcJson.Default.PipeMessage)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_accepting is not null)
            await _accepting.ConfigureAwait(false);
        Connection[] open;
        lock (_lock)
            open = [.. _connections];
        var serving = open.Select(c => c.Run).OfType<Task>().ToArray();
        foreach (var connection in open)
            await connection.Pipe.DisposeAsync().ConfigureAwait(false); // ends the pending reads
        await Task.WhenAll(serving).ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed class Connection(PipeServer server, NamedPipeServerStream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public NamedPipeServerStream Pipe { get; } = pipe;

        /// <summary>Cancelled when the client goes away: long requests (run with wait) stop waiting.</summary>
        public CancellationTokenSource Gone { get; } = new();

        public volatile bool Subscribed;

        public Task? Run { get; set; }

        public async Task WriteAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            try
            {
                await _write.WaitAsync(server._stop.Token).ConfigureAwait(false);
                try
                {
                    await Pipe.WriteAsync(bytes, server._stop.Token).ConfigureAwait(false);
                    await Pipe.FlushAsync(server._stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    _write.Release();
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Client gone: its reader loop ends the connection.
            }
        }

        public void Dispose()
        {
            Gone.Dispose();
            _write.Dispose();
        }
    }
}
