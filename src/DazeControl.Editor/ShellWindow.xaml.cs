using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DazeControl.Editor.ViewModels;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Runs;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private bool _textFocused;
    private RunOverlayViewModel? _run;
    private readonly StepPaletteViewModel _palette = new();

    public ShellWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop(); // the stock backdrop handles theme switches itself (unlike OSD X-007)
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        AppWindow.SetIcon(Path.Combine(assets, "app.ico")); // taskbar, Alt+Tab
        // From code with a file path: an ms-appx IconSource in XAML blanks the whole window (OSD X-006).
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(assets, "app.png"))),
        };
        SecondsBox.Minimum = SettingsPageViewModel.MinPopupSeconds;
        SecondsBox.Maximum = SettingsPageViewModel.MaxPopupSeconds;
        VersionText.Text = "Version " + ShellViewModel.Version;
        AppFolderText.Text = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        InspectorPaneControl.WindowHandle = hwnd;
        LibraryViewControl.ViewModel = ViewModel.Library;
        RunsViewControl.ViewModel = ViewModel.Runs;
        RunsViewControl.OpenRequested += OnOpenRun;
        ViewModel.Library.FlowRequested += OnLibraryFlowRequested; // after the view model's own handler, which opens it
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
        if (ViewModel.Flows.CanUndoDelete)
        {
            // The deleted flow's desktop shortcut goes with the Undo bar: let the engine remove it before the process ends.
            args.Cancel = true;
            await ViewModel.Flows.EndUndoAsync();
            Close();
            return;
        }
        ViewModel.Session.Flush();
        if (!ViewModel.Session.HasUnsavedChanges)
            return;
        args.Cancel = true;
        if (await ConfirmLosingChangesAsync())
        {
            _closeAnyway = true;
            Close();
        }
    }

    /// <summary>True when nothing is unsaved, or the user chose to lose it. False while the question is already up.</summary>
    private async Task<bool> ConfirmLosingChangesAsync()
    {
        if (!await LeaveFlowAsync())
            return false;
        ViewModel.Session.Flush();
        if (!ViewModel.Session.HasUnsavedChanges)
            return true;
        if (_asking)
            return false;
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
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
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

    /// <summary>
    /// Leaving the open flow with a draft asks Save / Don't save / Cancel (D-047). True when the editor may close:
    /// nothing was unsaved, or the draft was saved or thrown away.
    /// </summary>
    private async Task<bool> LeaveFlowAsync()
    {
        if (_editor is not { HasChanges: true } editor)
            return true;
        if (_asking)
            return false;
        _asking = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = $"Save your changes to {editor.FlowName}?",
                Content = "If you don't, the flow stays as you last saved it.",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Don't save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            switch (await dialog.ShowAsync())
            {
                case ContentDialogResult.Primary:
                    return editor.Save(); // a failed write keeps the draft and says so
                case ContentDialogResult.Secondary:
                    editor.Discard();
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _asking = false;
        }
    }

    private async void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navigating || args.SelectedItemContainer?.Tag is not string tag || !Enum.TryParse<EditorPage>(tag, out var page))
            return;
        if (!await LeaveFlowAsync())
        {
            SelectFlowsPane(); // stay in the editor
            return;
        }
        if (page != EditorPage.Flows)
            _ = ViewModel.Flows.EndUndoAsync(); // leaving the page ends the chance to undo a Delete (D-076)
        ViewModel.Page = page;
        ViewModel.CloseFlow(); // Flows in the pane always means the list
        FlowsPage.Visibility = Show(page == EditorPage.Flows);
        LibraryPage.Visibility = Show(page == EditorPage.Library);
        if (page == EditorPage.Library)
            _ = ViewModel.Library.RefreshAsync();
        if (page == EditorPage.Runs)
            _ = ViewModel.Runs.RefreshAsync();
        SettingsPage.Visibility = Show(page == EditorPage.Settings);
        AboutPage.Visibility = Show(page == EditorPage.About);
        UpdatePages();
    }

    /// <summary>
    /// Flows while a flow is open: Flows is still the selected item, so SelectionChanged never fires. Leave the
    /// flow like the breadcrumb does (the D-047 prompt; Cancel stays in the flow) and show the list.
    /// </summary>
    private async void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_navigating || args.InvokedItemContainer != FlowsItem || ViewModel.Page != EditorPage.Flows
            || _editor is null)
            return;
        if (await LeaveFlowAsync())
            ViewModel.CloseFlow();
    }

    private bool _navigating;

    /// <summary>"Open in editor" on the Runs page: Flows in the pane again, without closing the flow it opens.</summary>
    private void OnOpenRun(Core.Runs.RunRecord run)
    {
        SelectFlowsPane();
        ViewModel.OpenRun(run);
        UpdatePages();
    }

    /// <summary>Library › "Used by" › a flow: the view model has opened it; Flows in the pane again, like a run.</summary>
    private void OnLibraryFlowRequested(string flowId)
    {
        if (ViewModel.Editor?.FlowId != flowId)
            return;
        SelectFlowsPane();
        UpdatePages();
    }

    private void SelectFlowsPane()
    {
        _navigating = true;
        try
        {
            Navigation.SelectedItem = FlowsItem;
        }
        finally
        {
            _navigating = false;
        }
        FlowsPage.Visibility = Visibility.Visible;
        LibraryPage.Visibility = SettingsPage.Visibility = AboutPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>The flow editor, the Runs page and the scrolled pages take the same place.</summary>
    private void UpdatePages()
    {
        var editing = _editor is not null;
        FlowEditorPage.Visibility = Show(editing);
        RunsPage.Visibility = Show(!editing && ViewModel.Page == EditorPage.Runs);
        PagesScroller.Visibility = Show(!editing && ViewModel.Page != EditorPage.Runs);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The card's context menu: Open.</summary>
    private void OnFlowOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.OpenFlow(id);
    }

    /// <summary>Enter, Space or UIA Invoke on a Flows card (D-070).</summary>
    private void OnFlowCardInvoked(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.OpenFlow(id);
    }

    /// <summary>The card's context menu: Delete (D-076); the Undo bar shows.</summary>
    private void OnFlowDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.Flows.Delete(id);
    }

    /// <summary>Delete on a focused card: as the menu's Delete, which ignores it while it's off.</summary>
    private void OnFlowCardDeleteRequested(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.Flows.Delete(id);
    }

    /// <summary>A click on Run, on or off, never opens the flow.</summary>
    private void OnRunHostTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    /// <summary>The card's context menu: Add to desktop / Remove from desktop (D-049).</summary>
    private void OnFlowDesktopClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            _ = ViewModel.Flows.ToggleDesktopAsync(id);
    }

    /// <summary>A click on a flow's card opens it, unless it landed on the On switch or a button inside the card.</summary>
    private void OnFlowCardTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, sender); element = VisualTreeHelper.GetParent(element))
        {
            if (element is ToggleSwitch or ButtonBase)
                return;
        }
        if (sender is FrameworkElement { Tag: string id })
        {
            e.Handled = true;
            ViewModel.OpenFlow(id);
        }
    }

    private void OnFlowCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid card)
            card.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
    }

    private void OnFlowCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid card)
            card.ClearValue(Panel.BackgroundProperty); // back to the SettingsCard style's fill
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
        if (_run is not null)
            _run.PropertyChanged -= OnRunChanged;
        _run = ViewModel.Run;
        if (_run is not null)
            _run.PropertyChanged += OnRunChanged;
        UpdateRun();
        if (ViewModel.Inspector is { } inspector)
            _ = inspector.RefreshShortcutProblemsAsync();
        UpdateHistory();
        UpdateDraft();
        UpdateOutline();
        UpdateProblems();
        UpdatePages();
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
                UpdateDraft();
                break;
            case nameof(FlowCanvasViewModel.HasChanges):
                UpdateDraft();
                break;
            case nameof(FlowCanvasViewModel.Outline):
                UpdateOutline();
                break;
            case nameof(FlowCanvasViewModel.Problems):
                UpdateProblems();
                break;
            case nameof(FlowCanvasViewModel.SelectedIds):
                DispatcherQueue.TryEnqueue(SelectInOutline); // never inside the tree's own input handling
                break;
        }
    }

    private void UpdateBreadcrumb() =>
        FlowBreadcrumb.ItemsSource = _editor is null ? null : new[] { "Flows", _editor.FlowName };

    private async void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0 && await LeaveFlowAsync())
            ViewModel.CloseFlow();
    }

    // The click moved focus off an inspector box; its binding commits on LostFocus, which comes after: save after it.
    private void OnSaveFlow(object sender, RoutedEventArgs e)
    {
        if (_editor is { } editor)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => editor.Save());
    }

    /// <summary>A focused inspector box may hold an edit not committed yet: Save stays on so a click can commit it.</summary>
    private void OnInspectorFocusChanged(object sender, RoutedEventArgs e)
    {
        _textFocused = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox box && IsInside(box, InspectorPaneControl);
        UpdateDraft();
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (ReferenceEquals(e, ancestor))
                return true;
        }
        return false;
    }

    /// <summary>Ctrl+S: commit a focused inspector box first (its binding updates on LostFocus), then save.</summary>
    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_editor is not { CanEdit: true } editor)
            return;
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is not TextBox)
        {
            editor.Save();
            return;
        }
        _ = FlowCanvasControl.Focus(FocusState.Programmatic) || FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = Content.XamlRoot.Content });
        // LostFocus (and with it the binding) arrives after this handler: save once it has.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => editor.Save());
    }

    private void OnCancelFlow(object sender, RoutedEventArgs e) => _editor?.Discard();

    private void UpdateDraft()
    {
        var changed = _editor is { HasChanges: true };
        SaveButton.IsEnabled = (changed || _textFocused) && _editor is { CanEdit: true };
        CancelButton.IsEnabled = changed && _editor!.CanEdit;
        DraftText.Visibility = Show(changed);
        ToolTipService.SetToolTip(RunButton, changed
            ? "Run this flow as last saved. Save first to run your changes, or try them with Dry run."
            : "Run this flow now");
    }

    // ---- Runs on the canvas (UX §5.5) ----

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e) => UpdateRun();

    private void UpdateRun()
    {
        var run = _run;
        RunButton.IsEnabled = DryRunButton.IsEnabled = run?.CanRun == true;
        var running = run?.IsRunning == true;
        RunButton.Visibility = DryRunButton.Visibility = Show(!running);
        RunStatusItem.Visibility = StopButton.Visibility = Show(running);
        StopButton.IsEnabled = run?.CanStop == true;
        RunStatusText.Text = run?.Status ?? "";
        RunInfo.IsOpen = run?.IsInfoOpen == true;
        if (run is null)
            return;
        RunInfo.Title = run.InfoTitle;
        RunInfo.Message = run.InfoMessage;
        RunInfo.IsClosable = run.CanCloseInfo;
        RunInfo.Severity = run.InfoSeverity switch
        {
            RunInfoSeverity.Success => InfoBarSeverity.Success,
            RunInfoSeverity.Warning => InfoBarSeverity.Warning,
            RunInfoSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
    }

    private void OnRun(object sender, RoutedEventArgs e) => _run?.RunCommand.Execute(null);

    private void OnDryRun(object sender, RoutedEventArgs e) => _run?.DryRunCommand.Execute(null);

    private void OnStopRun(object sender, RoutedEventArgs e) => _run?.StopCommand.Execute(null);

    private void OnRunInfoClosed(InfoBar sender, InfoBarClosedEventArgs args) => _run?.CloseInfo();

    // ---- Layout (D-067, UX #63) ----

    private void OnLayoutMenuOpening(object? sender, object e)
    {
        LayoutMenu.Items.Clear();
        if (_editor is not { } editor)
            return;
        var o = editor.LayoutOptions;
        var spacing = editor.LayoutChoices.Spacing ?? Core.Layout.LayoutSpacing.Normal;
        LayoutMenu.Items.Add(LayoutChoice("Wrap long chains", "wrap", o.WrapAfter,
            [("After 3 steps", 3), ("After 5 steps", 5), ("After 8 steps", 8), ("Never", 0)], v => editor.SetLayout(wrapAfter: v)));
        LayoutMenu.Items.Add(LayoutChoice("Spacing", "spacing", spacing,
            [("Compact", Core.Layout.LayoutSpacing.Compact), ("Normal", Core.Layout.LayoutSpacing.Normal), ("Roomy", Core.Layout.LayoutSpacing.Roomy)],
            v => editor.SetLayout(spacing: v)));
        LayoutMenu.Items.Add(LayoutChoice("Line up", "align", o.Align,
            [("Centred", Core.Layout.LayoutAlign.Centre), ("Along the top", Core.Layout.LayoutAlign.Top)], v => editor.SetLayout(align: v)));
        LayoutMenu.Items.Add(LayoutChoice("Lines", "lines", o.Lines,
            [("Curved", Core.Layout.LayoutLines.Curved), ("Right angles", Core.Layout.LayoutLines.RightAngles)], v => editor.SetLayout(lines: v)));
        LayoutMenu.Items.Add(new MenuFlyoutSeparator());

        // Off with its reason (X-006): a disabled item shows no tooltip, so the reason sits where a shortcut would.
        var untangle = new MenuFlyoutItem { Text = "Untangle lines", Icon = new FontIcon { Glyph = "\uE8CB" }, IsEnabled = editor.CanUntangle };
        if (editor.UntangleReason is { } reason)
        {
            untangle.KeyboardAcceleratorTextOverride = reason;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(untangle, reason);
        }
        untangle.Click += (_, _) => editor.Untangle();
        LayoutMenu.Items.Add(untangle);
        var reset = new MenuFlyoutItem { Text = "Reset layout", IsEnabled = !editor.LayoutChoices.IsDefault };
        reset.Click += (_, _) => editor.ResetLayout();
        LayoutMenu.Items.Add(reset);
    }

    private static MenuFlyoutSubItem LayoutChoice<T>(string text, string group, T current, (string Text, T Value)[] choices, Action<T> pick)
    {
        var sub = new MenuFlyoutSubItem { Text = text };
        foreach (var (label, value) in choices)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "layout-" + group, IsChecked = EqualityComparer<T>.Default.Equals(value, current) };
            item.Click += (_, _) => pick(value);
            sub.Items.Add(item);
        }
        return sub;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _editor?.Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => _editor?.Redo();

    private void UpdateHistory()
    {
        UndoButton.IsEnabled = _editor is { CanUndo: true, CanEdit: true };
        RedoButton.IsEnabled = _editor is { CanRedo: true, CanEdit: true };
    }

    // ---- Problems (n) (UX §5.1) ----

    public static Brush ProblemBrush(bool isError) =>
        (Brush)Application.Current.Resources[isError ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"];

    private void UpdateProblems()
    {
        var problems = _editor?.Problems ?? [];
        ProblemsButton.Label = _editor?.ProblemsLabel ?? "Problems (0)";
        ProblemsList.ItemsSource = problems;
        ProblemsList.Visibility = Show(problems.Count > 0);
        NoProblemsText.Visibility = Show(problems.Count == 0);
    }

    private void OnProblemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ProblemItem problem || _editor is null)
            return;
        ProblemsFlyout.Hide();
        _editor.Show(problem); // the outline follows the selection too (OnEditorChanged)
        if (OutlineTree.Visibility == Visibility.Visible)
            OutlineTree.Focus(FocusState.Programmatic);
        else
            FlowCanvasControl.Focus(FocusState.Programmatic);
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

    private void OnCheckForUpdates(object sender, RoutedEventArgs e) => _ = ViewModel.Updates.CheckAsync();

    /// <summary>Unsaved edits are asked about first; the window closes only once the updater has the update.</summary>
    private async void OnRestartForUpdate(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLosingChangesAsync() || !ViewModel.Updates.Restart())
            return;
        _closeAnyway = true;
        Close();
    }

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
