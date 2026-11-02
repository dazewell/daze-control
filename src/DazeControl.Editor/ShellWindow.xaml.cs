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
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using DazeControl.Editor.Canvas;

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
    private readonly StepPaletteViewModel _palette = new();

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
        InspectorPaneControl.WindowHandle = hwnd;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));

        ViewModel.Session.PropertyChanged += OnSessionChanged;
        ViewModel.PropertyChanged += OnShellChanged;
        PaletteList.ItemsSource = new CollectionViewSource { Source = _palette.Groups, IsSourceGrouped = true, ItemsPath = new PropertyPath(nameof(PaletteGroup.Items)) }.View;
        FlowCanvasControl.FallbackTarget = OutlineTree;
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
        InspectorPaneControl.ViewModel = ViewModel.Inspector;
        if (ViewModel.Inspector is { } inspector)
            _ = inspector.RefreshShortcutProblemsAsync();
        UpdateHistory();
        UpdateOutline();
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
            case nameof(FlowCanvasViewModel.CanUndo) or nameof(FlowCanvasViewModel.CanRedo) or nameof(FlowCanvasViewModel.CanEdit):
                UpdateHistory();
                break;
            case nameof(FlowCanvasViewModel.Outline):
                UpdateOutline();
                break;
            case nameof(FlowCanvasViewModel.SelectedIds):
                DispatcherQueue.TryEnqueue(SelectInOutline); // never inside the tree's own input handling
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

    private void OnTidyUp(object sender, RoutedEventArgs e) => _editor?.TidyUp();

    private void OnUndo(object sender, RoutedEventArgs e) => _editor?.Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => _editor?.Redo();

    private void UpdateHistory()
    {
        UndoButton.IsEnabled = _editor is { CanUndo: true, CanEdit: true };
        RedoButton.IsEnabled = _editor is { CanRedo: true, CanEdit: true };
    }

    // ---- Palette (UX §5.1, §5.2) ----

    private void OnTogglePalette(object sender, RoutedEventArgs e) => PalettePane.Visibility = Show(PaletteToggle.IsChecked == true);

    private void OnPaletteSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _palette.Query = sender.Text;
        PaletteEmpty.Visibility = Show(_palette.IsEmpty);
    }

    private void OnPaletteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteItem item)
            FlowCanvasControl.AddFromPalette(item.Step);
    }

    private void OnPaletteDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not PaletteItem item || _editor is not { CanEdit: true })
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetText(FlowCanvas.PaletteDragPrefix + item.Step);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    // ---- Canvas | Outline (UX §5.2) ----

    private bool _syncingOutline;

    private void OnShowCanvas(object sender, RoutedEventArgs e) => ShowOutline(false);

    private void OnShowOutline(object sender, RoutedEventArgs e) => ShowOutline(true);

    private void ShowOutline(bool outline)
    {
        CanvasViewButton.IsChecked = !outline;
        OutlineViewButton.IsChecked = outline;
        OutlineTree.Visibility = Show(outline);
        FlowCanvasControl.Visibility = Show(!outline);
        if (outline)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, SelectInOutline); // rows exist only after layout
            OutlineTree.Focus(FocusState.Programmatic);
        }
        else
        {
            FlowCanvasControl.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateOutline()
    {
        _syncingOutline = true;
        OutlineTree.ItemsSource = _editor?.Outline;
        _syncingOutline = false;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, SelectInOutline);
    }

    /// <summary>
    /// The outline selects one row (TreeView multi-select doesn't redraw its check boxes from code reliably): on
    /// showing it, a canvas multi-selection narrows to the focused step so commands act only on what is visible.
    /// </summary>
    private void SelectInOutline()
    {
        if (_editor is null || OutlineTree.Visibility != Visibility.Visible)
            return;
        var id = _editor.FocusedId ?? (_editor.SelectedIds.Count > 0 ? _editor.SelectedIds.First() : null);
        if (_editor.SelectedIds.Count > 1)
            _editor.SelectOnly(id is null ? [] : [id]);
        var item = _editor.Outline.SelectMany(i => i.Children.Prepend(i)).FirstOrDefault(i => i.Id == id);
        _syncingOutline = true;
        try
        {
            if (item is not null && OutlineTree.ContainerFromItem(item) is TreeViewItem container
                && OutlineTree.NodeFromContainer(container) is { } node)
            {
                OutlineTree.SelectedNode = node;
                container.StartBringIntoView();
            }
            else
            {
                OutlineTree.SelectedNode = null;
            }
        }
        finally
        {
            _syncingOutline = false;
        }
    }

    private void OnOutlineSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_syncingOutline || _editor is null || sender.SelectedNode?.Content is not OutlineItem item)
            return;
        if (_editor.SelectedIds.Count != 1 || !_editor.SelectedIds.Contains(item.Id))
            _editor.SelectOnly([item.Id]);
    }

    private void OnOutlineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editor is null)
            return;
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        if (alt && e.Key is VirtualKey.Up or VirtualKey.Down && _editor.FocusedId is { } id && _editor.CanEdit)
        {
            _editor.MoveInColumn(id, e.Key == VirtualKey.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (FlowCanvasControl.RunCommandKey(e.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>Right-click, Shift+F10 or the menu key: the canvas's own command menu (UX §5.2).</summary>
    private void OnOutlineContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_editor is null)
            return;
        if ((args.OriginalSource as FrameworkElement)?.DataContext is OutlineItem item && !_editor.SelectedIds.Contains(item.Id))
            _editor.Select(item.Id);
        var target = args.OriginalSource as FrameworkElement ?? OutlineTree;
        Windows.Foundation.Point? point = args.TryGetPosition(OutlineTree, out var p) ? p : null;
        args.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            var menu = FlowCanvasControl.NodeMenu();
            if (point is { } at)
                menu.ShowAt(OutlineTree, at);
            else
                menu.ShowAt(target);
        });
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
