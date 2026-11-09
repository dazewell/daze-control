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

    public AudioSource() => _watcher = new DefaultAudioWatcher(() => Raised?.Invoke(new TriggerEvent("audio.default-changed")));

    public IReadOnlyList<string> Events { get; } = ["audio.default-changed"];

    public event Action<TriggerEvent>? Raised;

    public void Load() => _watcher.Start();

    public void Unload() => _watcher.Stop();

    public void Dispose() => _watcher.Dispose();
}
