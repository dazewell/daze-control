using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DazeControl.Ipc;

/// <summary>
/// The engine's end of the pipe (D-011): current user only, any number of clients, requests on one connection
/// handled concurrently (a <c>run --wait</c> doesn't block a <c>status</c>), answers and events written one line at a
/// time through a bounded per-connection queue: a client that stops reading is dropped, it never grows the engine's
/// memory. Idle cost: one pending overlapped connect, no thread. Never throws to its caller; failures go to
/// <c>onError</c>. Exit: <see cref="StopAcceptingAsync"/>, then <see cref="DrainAsync"/> (pending answers are
/// written), then dispose.
/// </summary>
/// <param name="pipeName"><see cref="Protocol.CurrentPipeName"/> in the engine.</param>
/// <param name="handler">Answers one request; may run long (run with wait). Gets the request's connection token.</param>
/// <param name="onError">For the log.</param>
public sealed class PipeServer(string pipeName, Func<PipeRequest, CancellationToken, Task<PipeMessage>> handler,
    Action<string, Exception>? onError = null) : IAsyncDisposable
{
    /// <summary>Lines waiting for one client; a client this far behind is dropped.</summary>
    public const int QueueLimit = 256;

    private readonly CancellationTokenSource _accept = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly HashSet<Connection> _connections = [];
    private Task? _accepting;
    private int _disposed;

    public void Start() => _accepting = AcceptAsync();

    private void Report(string what, Exception e) => onError?.Invoke(what, e);

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
            _ = connection.Enqueue(line);
    }

    /// <summary>No new clients and no new requests on open connections; requests already taken still get answers.</summary>
    public async Task StopAcceptingAsync()
    {
        await _accept.CancelAsync().ConfigureAwait(false);
        if (_accepting is not null)
            await _accepting.ConfigureAwait(false);
    }

    /// <summary>
    /// After <see cref="StopAcceptingAsync"/>: waits until every connection has written its pending answers (a
    /// <c>run --wait</c> gets its result), or until <paramref name="cancellationToken"/> gives up.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        Task[] serving;
        lock (_lock)
            serving = [.. _connections.Select(c => c.Run).OfType<Task>()];
        try
        {
            await Task.WhenAll(serving).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Out of grace: DisposeAsync cuts the rest.
        }
    }

    private async Task AcceptAsync()
    {
        while (!_accept.IsCancellationRequested)
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
                await pipe.WaitForConnectionAsync(_accept.Token).ConfigureAwait(false);
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
        var writing = connection.WriteLoopAsync();
        var pending = new List<Task>();
        var stopping = false;
        try
        {
            // Pending pipe I/O holds the handle open (Dispose can't close it), so reads and writes end by
            // cancellation (CancelIoEx), never by disposing the pipe.
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(_accept.Token, connection.Gone.Token);
            using var reader = new StreamReader(connection.Pipe, new UTF8Encoding(false), leaveOpen: true);
            while (await reader.ReadLineAsync(reading.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(AnswerAsync(connection, line));
            }
        }
        catch (OperationCanceledException) when (_accept.IsCancellationRequested)
        {
            stopping = true; // keep the client: its pending answers are still written
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Client gone or dropped.
        }
        finally
        {
            if (!stopping)
                connection.Leave();
            await Task.WhenAll(pending).ConfigureAwait(false);
            connection.Complete();
            await writing.ConfigureAwait(false);
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
        await connection.Enqueue(JsonSerializer.Serialize(answer, IpcJson.Default.PipeMessage)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _accept.CancelAsync().ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_accepting is not null)
            await _accepting.ConfigureAwait(false);
        Connection[] open;
        lock (_lock)
            open = [.. _connections];
        var serving = open.Select(c => c.Run).OfType<Task>().ToArray();
        foreach (var connection in open)
        {
            connection.Leave(); // long requests stop waiting; with _stop, ends its pending reads and writes
        }
        await Task.WhenAll(serving).ConfigureAwait(false);
        _accept.Dispose();
        _stop.Dispose();
    }

    private sealed class Connection(PipeServer server, NamedPipeServerStream pipe) : IDisposable
    {
        private readonly Channel<(string Line, TaskCompletionSource? Written)> _queue =
            Channel.CreateBounded<(string Line, TaskCompletionSource? Written)>(new BoundedChannelOptions(QueueLimit)
            {
                SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
            });

        private int _left;
        private int _dropped;

        public NamedPipeServerStream Pipe { get; } = pipe;

        /// <summary>Cancelled when the client goes away: long requests (run with wait) stop waiting.</summary>
        public CancellationTokenSource Gone { get; } = new();

        public volatile bool Subscribed;

        public Task? Run { get; set; }

        /// <summary>Queues one line; completes once it is written or can't be. A full queue drops the client.</summary>
        public Task Enqueue(string line)
        {
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_queue.Writer.TryWrite((line, written)))
                return written.Task;
            if (_queue.Reader.Count >= QueueLimit && Interlocked.Exchange(ref _dropped, 1) == 0)
            {
                server.Report("pipe: client too slow, dropped", new IOException($"{QueueLimit} lines unread"));
                Leave(); // cancels its pending read and write; ServeAsync cleans up
            }
            return Task.CompletedTask;
        }

        /// <summary>No more lines; the write loop ends once the queue is written.</summary>
        public void Complete() => _queue.Writer.TryComplete();

        public void Leave()
        {
            if (Interlocked.Exchange(ref _left, 1) != 0)
                return;
            try
            {
                Gone.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already cleaned up.
            }
        }

        public async Task WriteLoopAsync()
        {
            // The pipe has no buffer of its own: a write completes only once the client reads it.
            TaskCompletionSource? inFlight = null;
            try
            {
                using var writing = CancellationTokenSource.CreateLinkedTokenSource(server._stop.Token, Gone.Token);
                await foreach (var (line, written) in _queue.Reader.ReadAllAsync(writing.Token).ConfigureAwait(false))
                {
                    inFlight = written;
                    await Pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), writing.Token).ConfigureAwait(false);
                    await Pipe.FlushAsync(writing.Token).ConfigureAwait(false);
                    written?.TrySetResult();
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Client gone: its reader loop ends the connection.
            }
            finally
            {
                inFlight?.TrySetResult(); // cut off mid-write: its answer waits no longer
                _queue.Writer.TryComplete();
                while (_queue.Reader.TryRead(out var item))
                    item.Written?.TrySetResult();
            }
        }

        public void Dispose() => Gone.Dispose();
    }
}
