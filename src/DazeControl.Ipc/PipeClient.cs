using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DazeControl.Ipc;

/// <summary>
/// A client of the engine's pipe (dctl, the editor). One request at a time per client: <see cref="SendAsync"/>
/// writes it and reads lines until the answer with the same id arrives, handing any events on the way to
/// <see cref="EventReceived"/>.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private int _nextId;

    private PipeClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
    }

    public event Action<PipeEvent>? EventReceived;

    /// <summary>Null when no engine is listening within <paramref name="timeout"/>.</summary>
    public static async Task<PipeClient?> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            return new PipeClient(pipe);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Sends <paramref name="request"/> (its id and protocol are filled in) and waits for its answer.</summary>
    /// <exception cref="IOException">The engine went away.</exception>
    public async Task<PipeMessage> SendAsync(PipeRequest request, CancellationToken cancellationToken = default)
    {
        var id = ++_nextId;
        var line = JsonSerializer.Serialize(new PipeRequest
        {
            Protocol = Protocol.Version, Id = id, Op = request.Op, Flow = request.Flow, Wait = request.Wait,
            DryRun = request.DryRun, Enable = request.Enable, Limit = request.Limit, Trigger = request.Trigger,
        }, IpcJson.Default.PipeRequest);
        await _pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var reply = await ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The engine closed the connection.");
            if (reply.Event is { } e && reply.Id == 0 && reply.Ok)
                EventReceived?.Invoke(e);
            else if (reply.Id == id || reply.Id == 0)
                return reply;
        }
    }

    /// <summary>The next message (after <see cref="Ops.Subscribe"/>: events); null when the engine has gone.</summary>
    public async Task<PipeMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
                return JsonSerializer.Deserialize(line, IpcJson.Default.PipeMessage);
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
