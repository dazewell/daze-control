using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Menus;

/// <summary>The open menu's controller feed: readings at 60 Hz for as long as the menu is up (T12.6).</summary>
public sealed class GamepadMenuFeed(GamepadService service) : IMenuPadFeed
{
    public IDisposable Start(Action<PadReading> onReading) => service.Listen(onReading);
}
