using DazeControl.Providers.Basic;

namespace DazeControl.Engine.Shell;

/// <summary><c>notify</c> text as a tray notification, marshalled to the message thread (D-022).</summary>
internal sealed class TrayNotifier(MessageWindow window, TrayIcon tray) : INotifier
{
    public void Show(string flowName, string text) => window.Post(() => tray.ShowBalloon(flowName, text));
}
