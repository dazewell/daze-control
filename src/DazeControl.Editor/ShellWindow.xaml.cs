using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DazeControl.Editor.ViewModels;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.FlowList;
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
using Rect = Windows.Foundation.Rect;

namespace DazeControl.Editor;

/// <summary>The editor window (UX §5.1). Pages are panels shown one at a time, as in the OSD's settings.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes, which ends the app.")]
public sealed partial class ShellWindow : Window
{
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _watchDelay;
    private bool _closeAnyway;
    private bool _asking;
    private FlowCanvasViewModel? _editor;
    // An inspector box holding text its binding has not committed yet (it commits on LostFocus): the only reason
    // Save is on without HasChanges. Focus alone is not one (the box's text still equals what it had on entry).
    private TextBox? _editBox;
    private string _editBaseline = "";
    private bool PendingText => _editBox is { IsLoaded: true } && _editBox.Text != _editBaseline;
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
        if (Motion.Enabled)
            DraftDot.OpacityTransition = new ScalarTransition { Duration = MotionTokens.Duration(true, MotionTokens.FadeMs) };
        SecondsBox.Minimum = SettingsPageViewModel.MinPopupSeconds;
        SecondsBox.Maximum = SettingsPageViewModel.MaxPopupSeconds;
        ViewModel.Settings.StepDefaults.Rows.CollectionChanged += (_, _) => ShowStepDefaultsEmpty();
        ShowStepDefaultsEmpty();
        VersionText.Text = "Version " + ShellViewModel.Version;
        AppFolderText.Text = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        InspectorPaneControl.WindowHandle = hwnd;
        LibraryViewControl.ViewModel = ViewModel.Library;
        RunsViewControl.ViewModel = ViewModel.Runs;
        RunsViewControl.OpenRequested += OnOpenRun;
        ViewModel.Library.FlowRequested += OnLibraryFlowRequested; // after the view model's own handler, which opens it
        ViewModel.CalledFlowRequested += OnCalledFlowRequested;
        ViewModel.LibraryRequested += OnLibraryRequested;
        WindowPlacementInterop.Apply(this, ViewModel.EditorSettings?.Settings.Window); // D-098

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
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                ViewModel.Flows.RefreshTimes(); // no timer ticks them (D-009)
        };
        AppWindow.Closing += OnClosing;
        HookBackForward(hwnd);
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
        SaveWindowPlacement(); // the store writes it when the app ends (D-098)
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

    private void SaveWindowPlacement()
    {
        if (ViewModel.EditorSettings is { } store && WindowPlacementInterop.Capture(this) is { } placement)
            store.Update(s => s.Window = placement);
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
        ShowPage(page);
    }

    private void ShowPage(EditorPage page)
    {
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

    // ---- Back / forward (D-095): mouse side buttons, Alt+Left/Right, touchpad gestures (WM_APPCOMMAND) ----

    private const uint WmAppCommand = 0x0319;
    private const int AppCommandBack = 1;
    private const int AppCommandForward = 2;
    private const nuint SubclassId = 0xDC01;
    private long _lastBackForwardTick;
    private bool _lastWasForward;
    private SubclassProc? _subclass; // kept alive for the window's life

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    private void HookBackForward(nint hwnd)
    {
        if (Content is UIElement root)
        {
            // handledEventsToo: a card or text box under the pointer may have marked the click handled.
            root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleasedBackForward), true);
            // The accelerators live on the window root: left on Auto, WinUI pops an "Alt+Left" tooltip over whatever has focus.
            root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
            foreach (var (key, forward) in new[] { (VirtualKey.Left, false), (VirtualKey.Right, true) })
            {
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
                accelerator.Invoked += (sender, args) =>
                {
                    if (!forward && FocusManager.GetFocusedElement(root.XamlRoot) is FlowCard)
                        return; // Alt+Left moves the card out of its group (D-088)
                    args.Handled = true;
                    _ = GoBackForwardAsync(forward);
                };
                root.KeyboardAccelerators.Add(accelerator);
            }
        }
        _subclass = (h, msg, w, l, id, data) =>
        {
            if (msg == WmAppCommand)
            {
                var command = (int)(((long)l >> 16) & 0x0FFF);
                if (command is AppCommandBack or AppCommandForward)
                {
                    _ = GoBackForwardAsync(command == AppCommandForward);
                    return 1; // handled
                }
            }
            return DefSubclassProc(h, msg, w, l);
        };
        SetWindowSubclass(hwnd, _subclass, SubclassId, 0);
    }

    private void OnPointerReleasedBackForward(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint(null).Properties.PointerUpdateKind;
        if (kind is PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released)
        {
            e.Handled = true;
            _ = GoBackForwardAsync(kind == PointerUpdateKind.XButton2Released);
        }
    }

    /// <summary>
    /// Back or Forward: the place the history names, after the D-047 prompt when a draft is open (Cancel stays).
    /// One mouse click can arrive as a pointer event and as WM_APPCOMMAND: a second one in the same direction within 300 ms is ignored.
    /// </summary>
    private async Task GoBackForwardAsync(bool forward)
    {
        var now = Environment.TickCount64;
        if ((now - _lastBackForwardTick < 300 && forward == _lastWasForward) || _asking || ViewModel.Target(forward) is null)
            return;
        _lastBackForwardTick = now;
        _lastWasForward = forward;
        if (!await LeaveFlowAsync())
            return;
        if (ViewModel.Target(forward) is not { } target) // the engine's file may have changed while the prompt was up
            return;
        _ = ViewModel.Flows.EndUndoAsync(); // leaving the page ends the chance to undo a Delete (D-076)
        ViewModel.Go(forward);
        if (target.FlowId is null)
        {
            var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()
                .FirstOrDefault(i => i.Tag as string == target.Page.ToString());
            _navigating = true;
            try
            {
                Navigation.SelectedItem = item;
            }
            finally
            {
                _navigating = false;
            }
            ShowPage(target.Page);
        }
        else
        {
            SelectFlowsPane();
        }
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

    /// <summary>"Run a flow" › Open this flow: leave this one like the breadcrumb does (D-047), then open the called flow.</summary>
    private async void OnCalledFlowRequested(string flowId)
    {
        if (!await LeaveFlowAsync())
            return;
        if (ViewModel.Editor?.FlowId == flowId)
            ViewModel.CloseFlow(); // a discarded draft must not linger as the open editor; another flow is replaced in one history step
        ViewModel.OpenFlow(flowId);
        UpdatePages();
    }

    /// <summary>
    /// Android input › "Add inputs in the Library" (L1): Library in the pane; its SelectionChanged leaves the flow with
    /// the D-047 prompt, and Cancel there keeps the flow open.
    /// </summary>
    private void OnLibraryRequested() => Navigation.SelectedItem = LibraryNavItem;

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

    /// <summary>For a control whose slot stays reserved (D-104): invisible, and disabled alongside, instead of collapsed.</summary>
    public static double Fade(bool visible) => visible ? 1 : 0;

    /// <summary>Visible when <paramref name="flag"/> equals <paramref name="expected"/>.</summary>
    public static Visibility VisibleWhen(bool flag, bool expected) => flag == expected ? Visibility.Visible : Visibility.Collapsed;

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
            ViewModel.Flows.DeleteMany(ViewModel.Flows.ActionIds(id)); // the whole selection when the card is part of one (D-090)
    }

    /// <summary>Delete on a focused card: as the menu's Delete, which ignores it while it's off.</summary>
    private void OnFlowCardDeleteRequested(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.Flows.DeleteMany(ViewModel.Flows.ActionIds(id));
    }

    // ---- Selection and bulk actions (T16.6, DESIGN Part F, D-090) ----

    private static bool KeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Ctrl+Space toggles the focused card, Ctrl+A selects every row on screen, Esc clears (handled only when it did something).</summary>
    private void OnFlowCardSelectKey(object? sender, SelectKeyEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
            return;
        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.Flows.ToggleSelected(id);
                e.Handled = true;
                break;
            case VirtualKey.A:
                ViewModel.Flows.SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.Escape when ViewModel.Flows.HasSelection:
                ViewModel.Flows.ClearSelection();
                e.Handled = true;
                break;
        }
    }

    private void OnFlowCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.Flows.ToggleSelected(id);
    }

    private void OnGroupCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            ViewModel.Flows.ToggleGroupSelected(name);
    }

    private void OnBulkOnClick(object sender, RoutedEventArgs e) => ViewModel.Flows.SetSelectedEnabled(true);

    private void OnBulkOffClick(object sender, RoutedEventArgs e) => ViewModel.Flows.SetSelectedEnabled(false);

    private void OnBulkDeleteClick(object sender, RoutedEventArgs e) => ViewModel.Flows.DeleteSelected();

    private async void OnBulkGroupClick(object sender, RoutedEventArgs e)
    {
        if (await AskGroupNameAsync("New group", "", "Create") is { } name)
            ViewModel.Flows.MoveToGroup(ViewModel.Flows.SelectedIds, name);
    }

    /// <summary>The command bar's "Move to": No group, each group, New group… for the whole selection.</summary>
    private void OnBulkMoveOpening(object? sender, object e)
    {
        var menu = BulkMoveFlyout.Items;
        menu.Clear();
        AddBulkMoveItems(menu);
    }

    private void AddBulkMoveItems(IList<MenuFlyoutItemBase> items)
    {
        var ids = ViewModel.Flows.SelectedIds;
        AddMoveItem(items, "No group", ids, null);
        foreach (var group in ViewModel.Flows.GroupNames)
            AddMoveItem(items, group, ids, group);
        items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "New group…" };
        create.Click += async (_, _) =>
        {
            if (await AskGroupNameAsync("New group", "", "Create") is { } name)
                ViewModel.Flows.MoveToGroup(ids, name);
        };
        items.Add(create);
    }

    private void AddMoveItem(IList<MenuFlyoutItemBase> items, string text, IReadOnlyList<string> ids, string? group)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => ViewModel.Flows.MoveToGroup(ids, group);
        items.Add(item);
    }

    /// <summary>Right-click, Menu key, Shift+F10 on a group header: opens the same menu as its "..." button.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML event handlers are instance methods.")]
    private void OnGroupContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is Grid grid && grid.Children.OfType<Button>().LastOrDefault() is { Flyout: { } flyout } button)
        {
            e.Handled = true;
            flyout.ShowAt(button);
        }
    }

    /// <summary>A click on a group header's row (not its buttons) expands or collapses it.</summary>
    private void OnGroupHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, sender); element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
                return;
        }
        if (sender is FrameworkElement { Tag: string name })
        {
            e.Handled = true;
            if (KeyDown(VirtualKey.Control) || KeyDown(VirtualKey.Shift) || ViewModel.Flows.SelectMode)
                ViewModel.Flows.ToggleGroupSelected(name); // a header selects its members (D-090)
            else
                ViewModel.Flows.ToggleGroup(name);
        }
    }

    private void OnGroupUngroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            ViewModel.Flows.Ungroup(name);
    }

    private void OnGroupAllOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            ViewModel.Flows.SetGroupEnabled(name, true);
    }

    private void OnGroupAllOffClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            ViewModel.Flows.SetGroupEnabled(name, false);
    }

    private void OnGroupUpClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            ViewModel.Flows.MoveGroup(name, -1);
    }

    private void OnGroupDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            ViewModel.Flows.MoveGroup(name, 1);
    }

    private async void OnGroupRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name })
            return;
        if (await AskGroupNameAsync("Rename group", name, "Rename") is { } newName)
            ViewModel.Flows.RenameGroup(name, newName);
    }

    /// <summary>The card menu's "Move to ›": No group, each group, New group… (D-087). Filled on opening, as the groups change.</summary>
    private void OnFlowMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout { Target: FrameworkElement { Tag: string id } } flyout
            || flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault() is not { } sub)
            return;
        var ids = ViewModel.Flows.ActionIds(id);
        var many = ids.Count > 1;
        // One flow keeps the full menu; for a selection the one-flow items (Open, Run, Move up/down, desktop shortcut) step aside.
        var one = ViewModel.Flows.Items.FirstOrDefault(i => i.Id == id);
        foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
        {
            switch (Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item))
            {
                case "OneFlowOnly":
                    item.Visibility = many ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "FlowTrayShow" when item is ToggleMenuFlyoutItem show:
                    show.IsEnabled = ViewModel.Flows.CanEdit;
                    show.IsChecked = ViewModel.Flows.Items.Where(i => ids.Contains(i.Id)).All(i => !i.NotInTray);
                    break;
                case "FlowTrayLine" when item is ToggleMenuFlyoutItem line:
                    line.IsEnabled = ViewModel.Flows.CanEdit;
                    line.IsChecked = ViewModel.Flows.Items.Where(i => ids.Contains(i.Id)).All(i => i.LineAbove);
                    break;
                case "FlowDuplicate":
                    item.IsEnabled = ViewModel.Flows.CanEdit;
                    break;
                case "FlowDelete":
                    item.Text = many ? $"Delete {ids.Count} flows" : "Delete";
                    item.IsEnabled = many ? ViewModel.Flows.CanEdit && ViewModel.Flows.DeleteOffReason(ids) is null : one?.CanDelete ?? false;
                    break;
                case "FlowDeleteReason":
                    item.Text = (many ? ViewModel.Flows.DeleteOffReason(ids) : one?.DeleteOffReason) ?? "";
                    item.Visibility = item.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        }
        var current = one?.Group;
        sub.Items.Clear();
        if (many)
        {
            AddBulkMoveItems(sub.Items);
            return;
        }
        sub.Items.Add(MoveToItem("No group", id, null, current is null));
        foreach (var group in ViewModel.Flows.GroupNames)
            sub.Items.Add(MoveToItem(group, id, group, StringComparer.CurrentCultureIgnoreCase.Equals(group, current)));
        sub.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "New group…" };
        create.Click += async (_, _) =>
        {
            if (await AskGroupNameAsync("New group", "", "Create") is { } name)
                ViewModel.Flows.MoveFlowToGroup(id, name);
        };
        sub.Items.Add(create);
    }

    private ToggleMenuFlyoutItem MoveToItem(string text, string flowId, string? group, bool isCurrent)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isCurrent };
        item.Click += (_, _) => ViewModel.Flows.MoveFlowToGroup(flowId, group);
        return item;
    }

    /// <summary>Asks for a group name (not empty, 60 characters at most); null when cancelled.</summary>
    private async Task<string?> AskGroupNameAsync(string title, string initial, string button)
    {
        if (_asking)
            return null;
        _asking = true;
        try
        {
            var box = new TextBox { Text = initial, PlaceholderText = "Group name", MaxLength = FlowListModel.MaxGroupNameLength };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Group name");
            box.SelectAll();
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = box,
                PrimaryButtonText = button,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = FlowListModel.IsValidGroupName(initial),
            };
            box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = FlowListModel.IsValidGroupName(box.Text);
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>A click on Run, on or off, never opens the flow.</summary>
    private void OnRunHostTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    /// <summary>The card's context menu: Add to desktop / Remove from desktop (D-049).</summary>
    private void OnFlowDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.Flows.Duplicate(ViewModel.Flows.ActionIds(id));
    }

    // The toggle has already flipped when Click fires: IsChecked is the state the user asked for.
    private void OnFlowTrayShowClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string id } item)
            ViewModel.Flows.SetShowInTray(ViewModel.Flows.ActionIds(id), item.IsChecked);
    }

    private void OnFlowTrayLineClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string id } item)
            ViewModel.Flows.SetLineAbove(ViewModel.Flows.ActionIds(id), item.IsChecked);
    }

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
            if (KeyDown(VirtualKey.Shift))
                ViewModel.Flows.SelectRange(id);
            else if (KeyDown(VirtualKey.Control) || ViewModel.Flows.SelectMode)
                ViewModel.Flows.ToggleSelected(id); // additive: a plain click still opens (D-090)
            else
                ViewModel.OpenFlow(id);
        }
    }

    // Hover fades follow the Windows "Animation effects" setting (D-097): the transitions are only attached when it is on.
    private void OnFlowRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid card)
            return;
        var duration = MotionTokens.Duration(Motion.Enabled, MotionTokens.FadeMs);
        card.BackgroundTransition = duration == TimeSpan.Zero ? null : new BrushTransition { Duration = duration };
        foreach (var child in card.Children)
            if (child is FrameworkElement { Name: "Grip" } grip)
                grip.OpacityTransition = duration == TimeSpan.Zero ? null : new ScalarTransition { Duration = duration };
    }

    private void OnFlowCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid card)
        {
            card.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
            if (card.Tag is string id && ViewModel.Flows.Items.FirstOrDefault(i => i.Id == id) is { } item)
                item.IsHovered = true;
        }
    }

    private void OnFlowCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid card)
        {
            card.ClearValue(Panel.BackgroundProperty); // back to the SettingsCard style's fill
            if (card.Tag is string id && ViewModel.Flows.Items.FirstOrDefault(i => i.Id == id) is { } item)
                item.IsHovered = false;
        }
    }

    // ---- Sort, search, filter (T16.5, DESIGN Part E) ----

    /// <summary>Ctrl+F on the Flows page: into the search box, its text selected.</summary>
    private void OnFlowSearchAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FlowSearchBox.Focus(FocusState.Keyboard);
        FlowSearchBox.SelectAll();
    }

    /// <summary>Esc empties the search box.</summary>
    private void OnFlowSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && FlowSearchBox.Text.Length > 0)
        {
            FlowSearchBox.Text = "";
            e.Handled = true;
        }
    }

    // ---- Reordering the Flows list (T16.4, DESIGN B2/B3) ----

    private (string Key, bool IsGroup)? _drag;
    private DropTarget? _dropTarget;

    private void OnFlowDragStarting(UIElement sender, DragStartingEventArgs e) => StartDrag(sender, e, isGroup: false);

    private void OnGroupDragStarting(UIElement sender, DragStartingEventArgs e) => StartDrag(sender, e, isGroup: true);

    private void StartDrag(UIElement sender, DragStartingEventArgs e, bool isGroup)
    {
        if (sender is not FrameworkElement { Tag: string key })
        {
            e.Cancel = true;
            return;
        }
        _drag = (key, isGroup);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(key);
        sender.Opacity = 0.5;
    }

    private void OnDragCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        sender.Opacity = 1;
        _drag = null;
        HideDropCues();
    }

    private void OnFlowListDragOver(object sender, DragEventArgs e)
    {
        if (_drag is not { } drag)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
        _dropTarget = Locate(e.GetPosition(FlowRepeater), drag.IsGroup);
        ShowDropCues(_dropTarget);
        // Edges scroll the page: a drag in the middle of the window is not near one.
        var y = e.GetPosition(PagesScroller).Y;
        if (y < 48)
            PagesScroller.ChangeView(null, Math.Max(0, PagesScroller.VerticalOffset - 24), null, disableAnimation: true);
        else if (y > PagesScroller.ActualHeight - 48)
            PagesScroller.ChangeView(null, PagesScroller.VerticalOffset + 24, null, disableAnimation: true);
    }

    private void OnFlowListDragLeave(object sender, DragEventArgs e)
    {
        _dropTarget = null;
        HideDropCues();
    }

    private void OnFlowListDrop(object sender, DragEventArgs e)
    {
        var target = _dropTarget;
        var drag = _drag;
        HideDropCues();
        _dropTarget = null;
        if (target is null || drag is not { } d)
            return;
        e.Handled = true;
        if (d.IsGroup)
            ViewModel.Flows.DropGroup(d.Key, target.BeforeId);
        else
            ViewModel.Flows.DropFlow(d.Key, target.Group, target.BeforeId);
    }

    private Rect RowBounds(int index)
    {
        var element = FlowRepeater.TryGetElement(index) as FrameworkElement;
        return element is null ? Rect.Empty : element.TransformToVisual(FlowRepeater).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private DropTarget? Locate(Windows.Foundation.Point point, bool draggingGroup)
    {
        var rows = ViewModel.Flows.DropRows;
        if (rows.Count == 0)
            return null;
        var over = rows.Count - 1;
        var fraction = 1.0;
        for (var i = 0; i < rows.Count; i++)
        {
            var bounds = RowBounds(i);
            if (bounds.IsEmpty || point.Y > bounds.Bottom + 2)
                continue;
            over = i;
            fraction = bounds.Height > 0 ? Math.Clamp((point.Y - bounds.Top) / bounds.Height, 0, 1) : 0;
            break;
        }
        return DropMath.Resolve(rows, over, fraction, point.X >= RowFormat.IndentDips, draggingGroup);
    }

    private void ShowDropCues(DropTarget? target)
    {
        HideDropCues();
        if (target is null)
            return;
        if (target.HighlightGroup is { } group)
        {
            var at = ViewModel.Flows.DropRows.ToList().FindIndex(r => r.IsHeader && r.Key == group);
            var bounds = at < 0 ? Rect.Empty : RowBounds(at);
            if (bounds.IsEmpty)
                return;
            DropBox.Margin = new Thickness(0, bounds.Top, 0, 0);
            DropBox.Height = bounds.Height;
            DropBox.Visibility = Visibility.Visible;
            return;
        }
        var count = ViewModel.Flows.DropRows.Count;
        var line = target.LineIndex < count ? RowBounds(target.LineIndex) : RowBounds(count - 1);
        if (line.IsEmpty)
            return;
        var top = target.LineIndex < count ? line.Top - 3 : line.Bottom + 1;
        DropLine.Margin = new Thickness(target.Indented ? RowFormat.IndentDips : 0, top, 0, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private void HideDropCues()
    {
        DropLine.Visibility = Visibility.Collapsed;
        DropBox.Visibility = Visibility.Collapsed;
    }

    private void OnFlowCardMoveRequested(object? sender, int delta)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            ViewModel.Flows.MoveFlow(id, delta);
            RefocusCard(id);
        }
    }

    private void OnFlowCardTakeOutRequested(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            ViewModel.Flows.TakeOut(id);
            RefocusCard(id);
        }
    }

    private void OnFlowMoveUpClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.Flows.MoveFlow(id, -1);
    }

    private void OnFlowMoveDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            ViewModel.Flows.MoveFlow(id, 1);
    }

    /// <summary>A moved row may be re-created: focus stays on the flow (DESIGN B3).</summary>
    private void RefocusCard(string id) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        for (var i = 0; i < ViewModel.Flows.Rows.Count; i++)
        {
            if (FlowRepeater.TryGetElement(i) is Border { Child: FlowCard card } && card.Tag as string == id)
            {
                card.Focus(FocusState.Keyboard);
                return;
            }
        }
    });

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
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                editor.Save();
                if (_editBox is not null)
                    _editBaseline = _editBox.Text; // still focused: what it holds is saved now
                UpdateDraft();
            });
    }

    /// <summary>A focused inspector box may hold an edit not committed yet: Save stays on so a click can commit it.</summary>
    private void OnInspectorGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TextBox box || !IsInside(box, InspectorPaneControl))
            return;
        EndEdit();
        _editBox = box;
        _editBaseline = box.Text;
        box.TextChanged += OnEditBoxTextChanged;
    }

    private void OnInspectorLostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _editBox))
            EndEdit();
    }

    private void EndEdit()
    {
        if (_editBox is null)
            return;
        _editBox.TextChanged -= OnEditBoxTextChanged;
        _editBox = null;
        UpdateDraft();
    }

    private void OnEditBoxTextChanged(object sender, TextChangedEventArgs e) => UpdateDraft();

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
        // One source for Save's state, the dot and the tooltips: the session's draft (or an uncommitted box), nothing else.
        var showDot = (changed || PendingText) && _editor is { CanEdit: true };
        SaveButton.IsEnabled = showDot;
        CancelButton.IsEnabled = changed && _editor!.CanEdit;
        DraftDot.Opacity = showDot ? 1 : 0;
        ToolTipService.SetToolTip(DraftSlot, showDot ? "Unsaved changes" : null);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DraftSlot, showDot ? "Unsaved changes" : "");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(DraftSlot, showDot ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        ToolTipService.SetToolTip(SaveButton, showDot
            ? "You have unsaved changes. Save them (Ctrl+S)"
            : "Save your changes to this flow (Ctrl+S)");
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

    private bool _layoutPicked;

    private void OnLayoutMenuOpening(object? sender, object e)
    {
        _layoutPicked = false;
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
        untangle.Click += (_, _) => _layoutPicked = editor.Untangle();
        LayoutMenu.Items.Add(untangle);
        var reset = new MenuFlyoutItem { Text = "Reset layout", IsEnabled = !editor.LayoutChoices.IsDefault };
        reset.Click += (_, _) => { editor.ResetLayout(); _layoutPicked = true; };
        LayoutMenu.Items.Add(reset);
    }

    /// <summary>After a pick the canvas has focus again, so Ctrl+Z undoes Untangle at once (canvas keys, UX §5.8).</summary>
    private void OnLayoutMenuClosed(object? sender, object e)
    {
        if (_layoutPicked)
            FlowCanvasControl.Focus(FocusState.Programmatic);
    }

    private MenuFlyoutSubItem LayoutChoice<T>(string text, string group, T current, (string Text, T Value)[] choices, Action<T> pick)
    {
        var sub = new MenuFlyoutSubItem { Text = text };
        foreach (var (label, value) in choices)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "layout-" + group, IsChecked = EqualityComparer<T>.Default.Equals(value, current) };
            item.Click += (_, _) => { pick(value); _layoutPicked = true; };
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

    /// <summary>Opens the config file in its associated app; with no file or no app for it, its folder opens instead.</summary>
    private void OnOpenConfigFile(object sender, RoutedEventArgs e)
    {
        var file = ViewModel.Session.Path;
        try
        {
            if (File.Exists(file))
            {
                using var _ = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                return;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No app for the file type: fall through to the folder.
        }
        TryOpenFolder(Path.GetDirectoryName(file)!);
    }

    private void OnOpenAppFolder(object sender, RoutedEventArgs e) => TryOpenFolder(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

    private static void TryOpenFolder(string folder)
    {
        try { OpenFolder(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    private void ShowStepDefaultsEmpty() =>
        StepDefaultsEmpty.Visibility = ViewModel.Settings.StepDefaults.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "Add a step type…" menu: types without an entry, by area as the palette groups them.</summary>
    private void OnAddDefaultOpening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        foreach (var area in ViewModel.Settings.StepDefaults.Addable.GroupBy(i => i.Area))
        {
            var sub = new MenuFlyoutSubItem { Text = area.Key };
            foreach (var item in area)
            {
                var entry = new MenuFlyoutItem { Text = item.Title, Tag = item.Step };
                entry.Click += (s, _) => ViewModel.Settings.StepDefaults.Add((string)((MenuFlyoutItem)s).Tag);
                sub.Items.Add(entry);
            }
            menu.Items.Add(sub);
        }
    }

    private static StepDefaultRow RowOf(object sender) => (StepDefaultRow)((FrameworkElement)sender).Tag;

    private void OnResetTimeout(object sender, RoutedEventArgs e) => RowOf(sender).ResetTimeout();

    private void OnResetRetryCount(object sender, RoutedEventArgs e) => RowOf(sender).ResetRetryCount();

    private void OnResetRetryDelay(object sender, RoutedEventArgs e) => RowOf(sender).ResetRetryDelay();

    private void OnResetPolicy(object sender, RoutedEventArgs e) => RowOf(sender).ResetPolicy();

    private void OnResetOnly(object sender, RoutedEventArgs e) => RowOf(sender).ResetOnly();

    private void OnRemoveDefault(object sender, RoutedEventArgs e) => ViewModel.Settings.StepDefaults.Remove(RowOf(sender).StepId);

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

    // DllImport: the source generator can't marshal a delegate parameter; the editor is not Native AOT.
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
