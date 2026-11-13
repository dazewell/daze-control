using DazeControl.Core.Input;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Inspector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;

namespace DazeControl.Editor.Inspector;

/// <summary>The flow editor's right pane (UX §5.4); all logic is in <see cref="InspectorViewModel"/>.</summary>
public sealed partial class InspectorPane : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly FontFamily Body = new("Segoe UI Variable Text");
    private InspectorViewModel? _viewModel;

    public InspectorPane()
    {
        InitializeComponent();
    }

    private void OnGroupLostFocus(object sender, RoutedEventArgs e) => _viewModel?.CommitFolder();

    /// <summary>The window the file picker belongs to (unpackaged apps must say).</summary>
    public nint WindowHandle { get; set; }

    public InspectorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel?.PreviewPolicy(null);
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel.InputAdded -= OnInputAdded;
                _viewModel.OutputAdded -= OnOutputAdded;
            }
            _viewModel = value;
            if (value is not null)
            {
                value.PropertyChanged += OnViewModelChanged;
                value.InputAdded += OnInputAdded;
                value.OutputAdded += OnOutputAdded;
            }
            GlyphGrid.ItemsSource = value?.GlyphOptions;
            Bindings.Update();
        }
    }

    // Another step or card selected (or Many mode): the pane fades to the new content instead of snapping (D-097).
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InspectorViewModel.NodeId) or nameof(InspectorViewModel.Mode))
            Motion.Swap(Scroller);
    }

    /// <summary>"Add an input" (F-009): the new row is scrolled into view and its name box focused, so typing starts at once.</summary>
    private void OnInputAdded(string name)
    {
        // The row's container is made after the collection change: look once the layout has caught up.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var index = _viewModel?.Inputs.ToList().FindIndex(i => i.Name == name) ?? -1;
            if (index < 0 || InputsList.ContainerFromIndex(index) is not FrameworkElement row)
                return;
            row.UpdateLayout();
            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            if (FindName(row, "Input name") is TextBox box && box.Focus(FocusState.Programmatic))
                box.SelectAll();
        });
    }

    /// <summary>"Add a value" (T23.7): as "Add an input", the new row's name box gets the focus.</summary>
    private void OnOutputAdded(string name)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var index = _viewModel?.Outputs.ToList().FindIndex(o => o.Name == name) ?? -1;
            if (index < 0 || OutputsList.ContainerFromIndex(index) is not FrameworkElement row)
                return;
            row.UpdateLayout();
            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            if (FindName(row, $"Name of the value {name}") is TextBox box && box.Focus(FocusState.Programmatic))
                box.SelectAll();
        });
    }

    private static DependencyObject? FindName(DependencyObject parent, string automationName)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(child) == automationName)
                return child;
            if (FindName(child, automationName) is { } found)
                return found;
        }
        return null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnBindDevices(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FlowInputViewModel input)
            input.OpenBoundDevices();
    }

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static FontFamily Font(bool code) => code ? Mono : Body;

    public static string BrowseName(string title) => $"Browse for {title.ToLowerInvariant()}";

    /// <summary>Enter commits a one-line box at once instead of on leaving it.</summary>
    private void OnCommitOnEnter(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox && FocusManager.FindNextElement(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = XamlRoot.Content }) is Control next)
        {
            // x:Bind TwoWay commits on LostFocus: moving focus is the reliable commit.
            next.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ParamFieldViewModel field || WindowHandle == 0)
            return;
        if (field.IsFolder)
        {
            var folders = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            folders.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folders, WindowHandle);
            if (await folders.PickSingleFolderAsync() is { } folder)
                field.Text = folder.Path;
            return;
        }
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        if (await picker.PickSingleFileAsync() is { } file)
            field.Text = file.Path;
    }

    private void OnDeviceRowLoaded(object sender, RoutedEventArgs e) => DisableRow(sender);

    // A recycled container shows another device without a new Loaded.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnDeviceRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e) => DisableRow(sender);

    /// <summary>A row that can't be picked (#26) disables its drop-down or list item, which also greys it out.</summary>
    internal static void DisableRow(object row)
    {
        if (row is not FrameworkElement { DataContext: DeviceOption option } element)
            return;
        DependencyObject? parent = element;
        while (parent is not null and not Microsoft.UI.Xaml.Controls.Primitives.SelectorItem)
            parent = VisualTreeHelper.GetParent(parent);
        if (parent is Control item)
            item.IsEnabled = option.IsEnabled;
    }

    public static string ExamplesName(string title) => $"Examples for {title}";

    private void OnSuggestionsOpened(object? sender, object e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamFieldViewModel { Spec.Suggest: { Source: Core.Catalog.SuggestSource.Devices } suggest })
            _ = _viewModel?.RefreshSuggestionsAsync(suggest.Kind);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnAppTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && sender.Tag is ParamFieldViewModel field)
            field.SetAppQuery(sender.Text);
    }

    /// <summary>Focus shows every app by name and asks the device once for a fresh list (D-093).</summary>
    private void OnAppFocused(object sender, RoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox { Tag: ParamFieldViewModel field } box)
            return;
        field.SetAppQuery("");
        box.IsSuggestionListOpen = field.AppRows.Count > 0;
        if (_viewModel is null)
            return;
        // Rows that arrive after the box opened empty are shown once they are in (the refresh's own update is queued first).
        _ = _viewModel.RefreshAppsAsync(field, refresh: true).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            if (box.FocusState != FocusState.Unfocused && field.AppRows.Count > 0)
                box.IsSuggestionListOpen = true;
        }), TaskScheduler.Default);
    }

    /// <summary>Choosing a row stores its package; other text is kept as typed, as the package.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnAppSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (sender.Tag is ParamFieldViewModel field)
            field.Text = args.ChosenSuggestion is AppItem app ? app.Package : args.QueryText.Trim();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnAppLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox { Tag: ParamFieldViewModel field } box && box.Text.Trim() != field.Text)
            field.Text = box.Text.Trim();
    }

    private void OnHintLinkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamFieldViewModel field)
            _viewModel?.OpenAndroidInputs(field);
    }

    private void OnUseScreensOnNow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamFieldViewModel field && _viewModel is not null)
            _ = _viewModel.UseScreensOnNowAsync(field);
    }

    private void OnRemoveFlow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FlowTokenViewModel { Owner: { } field } token)
            field.RemoveFlow(token);
    }

    private void OnFlowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!alt || e.Key is not (VirtualKey.Up or VirtualKey.Down) || (sender as FrameworkElement)?.Tag is not FlowTokenViewModel { Owner: { } field } token)
            return;
        var list = Ancestor<ItemsControl>((DependencyObject)sender);
        var by = e.Key == VirtualKey.Up ? -1 : 1;
        field.MoveFlow(token, by);
        e.Handled = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            list?.UpdateLayout();
            if (list?.ContainerFromIndex(token.Index + by) is DependencyObject row && Descendant<Button>(row) is { } remove)
                remove.Focus(FocusState.Keyboard);
        });
    }

    private void OnFlowChosen(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: FlowChoice choice, DataContext: ParamFieldViewModel field } box)
        {
            field.AddFlow(choice.Id);
            box.SelectedItem = null;
        }
    }

    private void OnRemoveToken(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is KeyTokenViewModel { Owner: { } field } token)
            field.RemoveToken(token);
    }

    private void OnTokenKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!alt || e.Key is not (VirtualKey.Up or VirtualKey.Down) || (sender as FrameworkElement)?.Tag is not KeyTokenViewModel { Owner: { } field } token)
            return;
        var list = Ancestor<ItemsControl>((DependencyObject)sender);
        var to = token.Index + (e.Key == VirtualKey.Up ? -1 : 1);
        field.MoveToken(token, e.Key == VirtualKey.Up ? -1 : 1);
        e.Handled = true;
        // The rows are rebuilt, so the focused × is gone: focus the moved key's × to allow another move.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            list?.UpdateLayout();
            if (list?.ContainerFromIndex(to) is DependencyObject row && Descendant<Button>(row) is { } remove)
                remove.Focus(FocusState.Keyboard);
        });
    }

    private static T? Ancestor<T>(DependencyObject? node) where T : class
    {
        while (node is not null and not T)
            node = VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private static T? Descendant<T>(DependencyObject node) where T : class
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
                return found;
        }
        return null;
    }

    // A ComboBox whose items were rebuilt while its selection was being committed ends up empty although the view model
    // holds the value (the card shows it, the box doesn't): put the view model's value back once the dust has settled.
    private void OnPickerLoaded(object sender, RoutedEventArgs e) => ResyncPicker((ComboBox)sender);

    private void OnPickerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (box.SelectedItem is null && !box.IsEditable)
            DispatcherQueue.TryEnqueue(() => ResyncPicker(box));
    }

    private static void ResyncPicker(ComboBox box)
    {
        var wanted = box.DataContext switch
        {
            ParamFieldViewModel { IsMixed: false } f => f.IsChoice ? (object?)f.ChoiceOption : f.Device,
            DevicePickerViewModel d => (object?)d.Device,
            _ => null,
        };
        if (wanted is not null && !Equals(box.SelectedItem, wanted) && box.Items.Contains(wanted))
            box.SelectedItem = wanted;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnKeyTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && sender.Tag is ParamFieldViewModel field)
            sender.ItemsSource = field.KeySuggestions(sender.Text);
    }

    private void OnKeyBoxFocused(object sender, RoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox { Tag: ParamFieldViewModel field } box)
            return;
        // Focus always offers every key: leftover text must not filter the list (it is not the value).
        box.ItemsSource = field.KeySuggestions("");
        box.IsSuggestionListOpen = box.Text.Trim().Length == 0;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnKeySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (sender.Tag is not ParamFieldViewModel field)
            return;
        field.AddToken(args.ChosenSuggestion as string ?? args.QueryText);
        sender.Text = "";
        sender.ItemsSource = field.KeySuggestions("");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnExamplesOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout { Target: FrameworkElement { Tag: ParamFieldViewModel field } } menu)
            return;
        menu.Items.Clear();
        foreach (var example in field.Examples)
        {
            var item = new MenuFlyoutItem { Text = example.Title };
            ToolTipService.SetToolTip(item, example.Value);
            item.Click += (_, _) => field.ApplyExample(example);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// "Insert value" (D-129, DESIGN §5.1): headers (not selectable, skipped by the arrow keys) and a row per value. A row puts
    /// its token at the caret in place of the selection and gives the focus back to the box.
    /// </summary>
#pragma warning disable CA1822 // XAML event handlers are instance methods
    private void OnValuesOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout { Target: Button { Tag: ParamFieldViewModel field, Parent: Grid { Children: [TextBox box, ..] } } } menu)
            return;
        FillValueMenu(menu, field.ValueGroups, row =>
        {
            if (box.Text != field.Text)
                field.Text = box.Text;
            var caret = field.InsertValue(row, box.SelectionStart, box.SelectionLength);
            box.Focus(FocusState.Programmatic);
            box.Select(Math.Min(caret, box.Text.Length), 0);
        });
    }

    /// <summary>"Use a value" on a number setting (T25.3): the same list as Insert value, without devices; a row replaces the number.</summary>
    private void OnNumberValuesOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout { Target: Button { Tag: ParamFieldViewModel field } } menu)
            return;
        FillValueMenu(menu, field.ValueGroups, field.UseValue);
    }

    /// <summary>A flow's value (T23.7): the same list, with every step's values, since all have run when it is filled.</summary>
    private void OnOutputValuesOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout { Target: Button { Tag: FlowOutputViewModel output, Parent: Grid { Children: [TextBox box, ..] } } } menu)
            return;
        if (box.Text != output.Text)
            output.Text = box.Text;
        FillValueMenu(menu, output.ValueGroups, row =>
        {
            var caret = output.InsertValue(row, box.SelectionStart, box.SelectionLength);
            box.Focus(FocusState.Programmatic);
            box.Select(Math.Min(caret, box.Text.Length), 0);
        });
    }

    private static void FillValueMenu(MenuFlyout menu, IReadOnlyList<ValueGroup> groups, Action<ValueChoice> pick)
    {
        menu.Items.Clear();
        foreach (var group in groups)
        {
            var first = !menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == group.Section);
            if (menu.Items.Count > 0 && first)
                menu.Items.Add(new MenuFlyoutSeparator());
            // "From earlier steps" is one section with a sub-header per step; the others are one header each.
            if (first)
                menu.Items.Add(new MenuFlyoutItem { Text = group.Section, IsEnabled = false });
            if (group.Header != group.Section)
                menu.Items.Add(new MenuFlyoutItem { Text = group.Header, IsEnabled = false, Padding = new Thickness(24, 4, 11, 4) });
            foreach (var row in group.Rows)
            {
                var item = new MenuFlyoutItem { Text = row.Title };
                AutomationProperties.SetName(item, row.AutomationName);
                if (row.Help.Length > 0)
                    ToolTipService.SetToolTip(item, row.Help);
                item.Click += (_, _) => pick(row);
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = ValueText.Footer, IsEnabled = false });
    }

    /// <summary>
    /// A pill (DESIGN §5.3): Change value, the words to show when it has no value, and Remove. Every choice is one write; the
    /// row is rebuilt by it, so nothing here keeps the old pill.
    /// </summary>
    private void OnPillClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PillSegment pill } button)
            return;
        var field = pill.Owner;
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var panel = new StackPanel { Spacing = 8, MinWidth = 240 };
        if (pill.Words)
        {
            var change = new Button { Content = "Change value…", HorizontalAlignment = HorizontalAlignment.Stretch };
            change.Click += (_, _) =>
            {
                flyout.Hide();
                var menu = new MenuFlyout();
                FillValueMenu(menu, field.ValueGroups, choice => field.ChangePill(pill, choice));
                menu.ShowAt(button);
            };
            panel.Children.Add(change);
            var fallback = new TextBox { Header = "If it has no value, show", Text = pill.Fallback };
            var applied = false;
            void Apply()
            {
                if (applied)
                    return;
                applied = true;
                if (fallback.Text != pill.Fallback)
                    field.SetPillFallback(pill, fallback.Text);
            }
            fallback.KeyDown += (_, k) =>
            {
                if (k.Key == Windows.System.VirtualKey.Enter)
                {
                    k.Handled = true;
                    Apply();
                    flyout.Hide();
                }
            };
            flyout.Closing += (_, _) => Apply();
            panel.Children.Add(fallback);
        }
        var remove = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Stretch };
        remove.Click += (_, _) =>
        {
            flyout.Hide();
            field.RemovePill(pill);
        };
        panel.Children.Add(remove);
        flyout.Content = panel;
        flyout.ShowAt(button);
    }

    private void OnPillKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && sender is Button { Tag: PillSegment pill })
        {
            e.Handled = true;
            pill.Owner.RemovePill(pill);
        }
    }

    /// <summary>"Values it gives": puts the display text on the clipboard; the row says "Copied" in place of "Used by".</summary>
    private void OnCopyGivenValue(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GivenValueViewModel row } || _viewModel is not { } vm)
            return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(row.CopyText);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        foreach (var other in vm.GivenValues)
            other.IsCopied = ReferenceEquals(other, row);
    }

#pragma warning restore CA1822

    private void OnDevicesOpened(object? sender, object e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamFieldViewModel { Spec.Library: { } kind })
            _ = _viewModel?.RefreshDevicesAsync(kind);
    }

    /// <summary>An input's default or a call's input picker: what's on this PC now (UX §5.4).</summary>
    private void OnPickerOpened(object? sender, object e)
    {
        if ((sender as FrameworkElement)?.Tag is DevicePickerViewModel picker)
            _ = _viewModel?.RefreshDevicesAsync(picker.Kind);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnRemoveInput(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FlowInputViewModel input)
            input.Remove();
    }

    private void OnRemoveOutput(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FlowOutputViewModel output)
            output.Remove();
    }

    private void OnPolicyHover(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out var index))
            _viewModel?.PreviewPolicy(index);
    }

    private void OnPolicyLeave(object sender, RoutedEventArgs e) => _viewModel?.PreviewPolicy(null);

    /// <summary>"If it fails, run…": a new step by area, or a step already in the flow (built when it opens).</summary>
    public static Visibility ShowDefault(bool isStep, bool isSet) => isStep && !isSet ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowSet(bool isStep, bool isSet) => isStep && isSet ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Inherited values read dimmer than the ones the step sets (DESIGN A5).</summary>
    public static double DimStep(bool isStep, bool isSet) => isStep && !isSet ? 0.6 : 1;

    public static double Dim(bool isSet) => isSet ? 1 : 0.6;

    public static double DimRow(BulkRowView view) => view.IsDim ? 0.6 : 1;

    private void OnUseForAllClosed(object sender, RoutedEventArgs e) => UseForAllFlyout.Hide();

    private void OnFallbackMenuOpening(object? sender, object e)
    {
        FallbackMenu.Items.Clear();
        if (_viewModel is not { } vm)
            return;
        var add = new MenuFlyoutSubItem { Text = "A new step", Icon = new FontIcon { Glyph = "" } };
        foreach (var area in StepPaletteViewModel.All.GroupBy(i => i.Area))
        {
            var group = new MenuFlyoutSubItem { Text = area.Key };
            foreach (var item in area)
            {
                var menuItem = new MenuFlyoutItem { Text = item.Title, Icon = new FontIcon { Glyph = item.Glyph } };
                menuItem.Click += (_, _) => vm.AddFallback(item.Step);
                group.Items.Add(menuItem);
            }
            add.Items.Add(group);
        }
        FallbackMenu.Items.Add(add);
        if (vm.FallbackTargets.Count == 0)
            return;
        FallbackMenu.Items.Add(new MenuFlyoutSeparator());
        FallbackMenu.Items.Add(new MenuFlyoutItem { Text = "A step in this flow", IsEnabled = false });
        foreach (var target in vm.FallbackTargets)
        {
            var menuItem = new MenuFlyoutItem { Text = target.Title };
            menuItem.Click += (_, _) => vm.LinkFallback(target.Id);
            FallbackMenu.Items.Add(menuItem);
        }
    }

    private void OnGlyphClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GlyphOption option)
            _viewModel?.PickGlyphCommand.Execute(option);
        GlyphFlyout.Hide();
    }

    private void OnShortcutRecorded(object? sender, string hotkey)
    {
        if ((sender as FrameworkElement)?.Tag is ShortcutViewModel shortcut)
            _viewModel?.SetShortcut(shortcut.Index, hotkey);
    }

    // Why the key was refused shows under the box (UX §5.7 H1–H4, H7) and is read out, since focus stays in the box.
    private void OnShortcutRejected(object? sender, HotkeyRejection reason)
    {
        if (sender is not HotkeyRecorder { Tag: ShortcutViewModel shortcut } recorder)
            return;
        _viewModel?.RejectShortcut(shortcut.Index, reason);
        if (shortcut.Rejection is { } text)
            recorder.Announce(text);
    }

    private void OnShortcutTypedRefused(object? sender, string text)
    {
        if ((sender as FrameworkElement)?.Tag is ShortcutViewModel shortcut)
            _viewModel?.RejectShortcutText(shortcut.Index, text);
    }

    private void OnShortcutRecording(object? sender, bool recording) => _viewModel?.SetRecordingShortcut(recording);
}
