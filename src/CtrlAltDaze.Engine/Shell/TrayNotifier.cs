using CtrlAltDaze.Providers.Basic;

namespace CtrlAltDaze.Engine.Shell;

/// <summary><c>notify</c> text as tray notifications, one at a time (D-132), marshalled to the message thread (D-022).</summary>
internal sealed class TrayNotifier : INotifier, IDisposable
{
    private readonly NoticeSpacer _spacer;

    public TrayNotifier(MessageWindow window, TrayIcon tray) =>
        _spacer = new NoticeSpacer((flow, text, sound) => window.Post(() => tray.ShowBalloon(flow, text, sound)), TimeProvider.System);

    public void Show(string flowName, string text, bool sound) => _spacer.Show(flowName, text, sound);

    public void Dispose() => _spacer.Dispose();
}
