using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Engine;

/// <summary>The editor's view of the engine; the editor works without it (UX §5.1).</summary>
public interface IEngineClient
{
    bool IsRunning { get; }

    /// <summary><see cref="IsRunning"/> changed. Any thread.</summary>
    event Action? StateChanged;

    /// <summary>Run and config events from the engine. Any thread.</summary>
    event Action<PipeEvent>? EventReceived;

    /// <summary>The answer, or null when no engine is running (or it went away mid-request).</summary>
    Task<PipeMessage?> SendAsync(PipeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Two pipe connections (D-011): one subscribed to events, which also tells whether the engine is there,
/// and one for requests, opened on demand. While the engine is gone it tries again every
/// <see cref="RetryDelay"/> — cheap, and only while the editor is open.
/// </summary>
public sealed class EngineClient : IEngineClient, IAsyncDisposable
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    private readonly string _pipeName;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private PipeClient? _requests;
    private Task? _loop;
    private volatile bool _running;

    public EngineClient(string? pipeName = null) => _pipeName = pipeName ?? Protocol.CurrentPipeName();

    public bool IsRunning => _running;

    public event Action? StateChanged;

    public event Action<PipeEvent>? EventReceived;

    public void Start() => _loop ??= Task.Run(() => WatchAsync(_stop.Token));

    public async Task<PipeMessage?> SendAsync(PipeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op is Ops.TestStep or Inspector.EngineGamepadRecorder.Op)
            return await SendAloneAsync(request, cancellationToken).ConfigureAwait(false);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _requests ??= await PipeClient.ConnectAsync(_pipeName, ConnectTimeout, cancellationToken).ConfigureAwait(false);
            if (_requests is null)
                return null;
            try
            {
                return await _requests.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await _requests.DisposeAsync().ConfigureAwait(false);
                _requests = null;
                return null;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// A step test (or a controller recording, up to 15 s) can take a minute: it gets a connection of its own so other requests don't queue behind it.
    /// Cancelling closes that connection, which the engine takes as "stop the test".
    /// </summary>
    private async Task<PipeMessage?> SendAloneAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var client = await PipeClient.ConnectAsync(_pipeName, ConnectTimeout, cancellationToken).ConfigureAwait(false);
        if (client is null)
            return null;
        await using (client.ConfigureAwait(false))
        {
            try
            {
                return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await PipeClient.ConnectAsync(_pipeName, ConnectTimeout, cancellationToken).ConfigureAwait(false);
                if (client is not null)
                {
                    await using (client.ConfigureAwait(false))
                    {
                        var answer = await client.SendAsync(new PipeRequest { Op = Ops.Subscribe }, cancellationToken).ConfigureAwait(false);
                        if (answer.Ok)
                        {
                            SetRunning(true);
                            while (await client.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
                            {
                                if (message.Event is { } e)
                                    EventReceived?.Invoke(e);
                            }
                        }
                    }
                }
            }
            catch (IOException)
            {
                // the engine went away; below
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            SetRunning(false);
            try
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SetRunning(bool running)
    {
        if (_running == running)
            return;
        _running = running;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        if (_requests is not null)
            await _requests.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _requestLock.Dispose();
    }
}
