using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DazeControl.Editor.ViewModels;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace DazeControl.Editor;

/// <summary>The editor window (UX §5.1). Pages are panels shown one at a time, as in the OSD's settings.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes, which ends the app.")]
public sealed partial class ShellWindow : Window
{
    private const int WidthDip = 1100;
    private const int HeightDip = 720;

    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _watchDelay;
    private bool _closeAnyway;
    private bool _asking;
    private FlowCanvasViewModel? _editor;

    public ShellWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop(); // the stock backdrop handles theme switches itself (unlike OSD X-007)
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SecondsBox.Minimum = SettingsPageViewModel.MinPopupSeconds;
        SecondsBox.Maximum = SettingsPageViewModel.MaxPopupSeconds;
        VersionText.Text = "Version " + ShellViewModel.Version;
        AppFolderText.Text = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));

        ViewModel.Session.PropertyChanged += OnSessionChanged;
        ViewModel.PropertyChanged += OnShellChanged;
        ShowOutsideChange();

        // Debounced: one save by another editor raises several events.
        _watchDelay = DispatcherQueue.CreateTimer();
        _watchDelay.Interval = TimeSpan.FromMilliseconds(250);
        _watchDelay.IsRepeating = false;
        _watchDelay.Tick += (_, _) => ViewModel.Session.CheckOutsideChange();
        var folder = Path.GetDirectoryName(ViewModel.Session.Path)!;
        if (TryCreate(folder))
        {
            _watcher = new FileSystemWatcher(folder, Path.GetFileName(ViewModel.Session.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(() => _watchDelay.Start());
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(() => _watchDelay.Start());
            _watcher.EnableRaisingEvents = true;
        }

        Activated += OnFirstActivated;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            _watcher?.Dispose();
            _watchDelay.Stop();
            ViewModel.Session.PropertyChanged -= OnSessionChanged;
        };
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>The engine makes this folder too; making it here lets the watcher start on a first launch.</summary>
    private static bool TryCreate(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A second start of the editor lands here (single instance).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        _ = SetForegroundWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
    }

    /// <summary>
    /// Last chance to save: after Closed the process ends, so a conflict or a failed write is asked about here,
    /// while the InfoBars can still offer Keep mine / Reload.
    /// </summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAnyway)
            return;
        ViewModel.Session.Flush();
        if (!ViewModel.Session.HasUnsavedChanges)
            return;
        args.Cancel = true;
        if (_asking)
            return;
        _asking = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Your latest changes aren't saved",
                Content = ViewModel.Session.OutsideChange == OutsideChange.Conflict
                    ? "config.yaml changed outside the editor. Keep yours or reload the file first, or close and lose your changes."
                    : ViewModel.Session.SaveError,
                PrimaryButtonText = "Close without saving",
                CloseButtonText = "Stay",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _closeAnyway = true;
                Close();
            }
        }
        finally
        {
            _asking = false;
        }
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        _ = ViewModel.Flows.RefreshRunsAsync();
        _ = ViewModel.Settings.RefreshAutostartAsync();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag || !Enum.TryParse<EditorPage>(tag, out var page))
            return;
        ViewModel.Page = page;
        ViewModel.CloseFlow(); // Flows in the pane always means the list
        FlowsPage.Visibility = Show(page == EditorPage.Flows);
        LibraryPage.Visibility = Show(page == EditorPage.Library);
        SettingsPage.Visibility = Show(page == EditorPage.Settings);
        AboutPage.Visibility = Show(page == EditorPage.About);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnEditFlow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.OpenFlow(id);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.Editor))
            return;
        if (_editor is not null)
            _editor.PropertyChanged -= OnEditorChanged;
        _editor = ViewModel.Editor;
        if (_editor is not null)
            _editor.PropertyChanged += OnEditorChanged;
        FlowCanvasControl.ViewModel = _editor;
        FlowEditorPage.Visibility = Show(_editor is not null);
        PagesScroller.Visibility = Show(_editor is null);
        UpdateBreadcrumb();
        if (_editor is not null)
            FlowCanvasControl.Focus(FocusState.Programmatic);
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FlowCanvasViewModel.FlowName):
                UpdateBreadcrumb();
                break;
            case nameof(FlowCanvasViewModel.IsMissing) when _editor?.IsMissing == true:
                ViewModel.CloseFlow(); // deleted outside the editor
                break;
            case nameof(FlowCanvasViewModel.ZoomText):
                ZoomButton.Label = _editor!.ZoomText;
                break;
        }
    }

    private void UpdateBreadcrumb() =>
        FlowBreadcrumb.ItemsSource = _editor is null ? null : new[] { "Flows", _editor.FlowName };

    private void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            ViewModel.CloseFlow();
    }

    private void OnFit(object sender, RoutedEventArgs e) => FlowCanvasControl.Fit();

    private void OnActualSize(object sender, RoutedEventArgs e) => FlowCanvasControl.ZoomToActualSize();

    private void OnZoomIn(object sender, RoutedEventArgs e) => FlowCanvasControl.ZoomBy(1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => FlowCanvasControl.ZoomBy(0.8);

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigSession.OutsideChange))
            ShowOutsideChange();
    }

    private void ShowOutsideChange()
    {
        ConflictBar.IsOpen = ViewModel.Session.OutsideChange == OutsideChange.Conflict;
        ReloadedBar.IsOpen = ViewModel.Session.OutsideChange == OutsideChange.Reloaded;
    }

    private void OnStartEngine(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DazeControl.exe")) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StartEngineButton.Content = "Couldn't start it";
            StartEngineButton.IsEnabled = false;
        }
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) => OpenFolder(Path.GetDirectoryName(ViewModel.Session.Path)!);

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e) => OpenFolder(ViewModel.Settings.LogsFolder);

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        var sdk = typeof(ShellWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "WindowsAppSdkVersion")?.Value ?? "(unknown)";
        var package = new DataPackage();
        package.SetText(ViewModel.Details(sdk));
        Clipboard.SetContent(package);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
