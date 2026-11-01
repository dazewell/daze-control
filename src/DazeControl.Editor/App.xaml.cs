using Microsoft.UI.Xaml;

namespace DazeControl.Editor;

public partial class App : Application
{
    private ShellWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new ShellWindow();
        _window.Activate();
    }
}
