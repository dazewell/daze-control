using DazeControl.Core.Model;
using DazeControl.Providers.Audio;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

/// <summary>
/// <c>audio.default-changed</c> (M20, T20.4): the default sound device changed. An IMMNotificationClient on its own
/// short-lived thread (<see cref="DefaultAudioWatcher"/>), registered only while a flow subscribes; the framework's
/// debounce folds the several roles Windows reports into one start.
/// </summary>
internal sealed class AudioSource : ITriggerSource
{
    private readonly DefaultAudioWatcher _watcher;

    public AudioSource() => _watcher = new DefaultAudioWatcher(OnDefault);

    public IReadOnlyList<string> Events { get; } = ["audio.default-changed"];

    public event Action<TriggerEvent>? Raised;

    public void Load() => _watcher.Start();

    public void Unload() => _watcher.Stop();

    public void Dispose() => _watcher.Dispose();

    /// <summary>
    /// Reads the new default's name on the lazy COM worker (D-009), not inside the audio service's callback, then raises it
    /// as <see cref="TriggerEvent.Detail"/> with the flow as <see cref="TriggerEvent.Kind"/> (T24.6). A name that can't be
    /// read still raises, without a detail, so a trigger with no <c>device</c> filter works as before.
    /// </summary>
    private async void OnDefault(AudioFlow flow, string endpointId)
    {
        string? name = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            name = await CoreAudioEndpoints.NameOfAsync(endpointId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Unknown name: raise without it.
        }
        try
        {
            Raised?.Invoke(new TriggerEvent("audio.default-changed", Detail: name, Kind: flow == AudioFlow.Input ? "input" : "output"));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The service logs its own failures; async void must not throw.
        }
    }
}
