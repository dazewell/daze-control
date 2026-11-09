using DazeControl.Engine.Shell;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

/// <summary>
/// <c>display.changed</c> (M20, T20.4): WM_DISPLAYCHANGE, which Windows sends to every top-level window when a
/// screen's resolution, layout or count changes. The engine's window is top-level, so no registration is needed; the
/// handler ignores the message unless a flow subscribes (<see cref="Load"/>). No thread, no timer.
/// </summary>
internal sealed class DisplaySource : ITriggerSource
{
    private const uint WM_DISPLAYCHANGE = 0x007E;

    private volatile bool _loaded;

    public DisplaySource(MessageWindow window) => window.Add(OnMessage);

    public IReadOnlyList<string> Events { get; } = ["display.changed"];

    public event Action<TriggerEvent>? Raised;

    public void Load() => _loaded = true;

    public void Unload() => _loaded = false;

    public void Dispose() => _loaded = false;

    private bool OnMessage(uint message, Windows.Win32.Foundation.WPARAM wParam, Windows.Win32.Foundation.LPARAM lParam)
    {
        if (message == WM_DISPLAYCHANGE && _loaded)
            Raised?.Invoke(new TriggerEvent("display.changed"));
        return false;
    }
}
