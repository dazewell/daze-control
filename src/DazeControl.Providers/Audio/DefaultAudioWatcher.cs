using DazeControl.Core.Model;

namespace DazeControl.Providers.Audio;

/// <summary>
/// Tells when the default sound device changes (T20.4, <c>audio.default-changed</c>): an IMMNotificationClient on a
/// dedicated MTA thread that exists only between <see cref="Start"/> and <see cref="Stop"/> (idle = zero, D-009).
/// The callback runs on a COM thread and only invokes <c>changed</c> with the flow and the new default's endpoint id (T24.6); no polling.
/// </summary>
public sealed class DefaultAudioWatcher(Action<AudioFlow, string> changed) : IDisposable
{
    private readonly Lock _lock = new();
    private ManualResetEventSlim? _stop;
    private Thread? _thread;

    public void Start()
    {
        lock (_lock)
        {
            if (_thread is not null)
                return;
            var stop = new ManualResetEventSlim();
            using var ready = new ManualResetEventSlim();
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Watch(stop, ready);
                }
                catch (Exception e)
                {
                    failure = e;
                    ready.Set();
                }
            })
            { IsBackground = true, Name = "audio-default-watcher" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            ready.Wait();
            if (failure is not null)
            {
                stop.Dispose();
                throw new InvalidOperationException("Couldn't watch the default sound device: " + failure.Message, failure);
            }
            (_thread, _stop) = (thread, stop);
        }
    }

    public void Stop()
    {
        Thread? thread;
        ManualResetEventSlim? stop;
        lock (_lock)
            (thread, stop, _thread, _stop) = (_thread, _stop, null, null);
        if (thread is null)
            return;
        stop!.Set();
        thread.Join(TimeSpan.FromSeconds(5));
        stop.Dispose();
    }

    public void Dispose() => Stop();

    private unsafe void Watch(ManualResetEventSlim stop, ManualResetEventSlim ready)
    {
        using var enumerator = CoreAudioEndpoints.Enumerator.Create();
        using var client = CoreAudioEndpoints.DeviceChangeClient.Create(() => { }, onDefault: true, defaultChanged: changed);
        enumerator.Value->RegisterEndpointNotificationCallback(client.Pointer);
        ready.Set();
        stop.Wait();
        enumerator.Value->UnregisterEndpointNotificationCallback(client.Pointer);
    }
}
