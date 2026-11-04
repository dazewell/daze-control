using DazeControl.Core.Input;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Inspector;
using Microsoft.UI.Xaml;
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

    /// <summary>The window the file picker belongs to (unpackaged apps must say).</summary>
    public nint WindowHandle { get; set; }

    public InspectorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel?.PreviewPolicy(null);
            _viewModel = value;
            GlyphGrid.ItemsSource = value?.GlyphOptions;
            Bindings.Update();
        }
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

    private void OnUseScreensOnNow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamFieldViewModel field && _viewModel is not null)
            _ = _viewModel.UseScreensOnNowAsync(field);
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnKeyTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && sender.Tag is ParamFieldViewModel field)
            sender.ItemsSource = field.KeySuggestions(sender.Text);
    }

    private void OnKeyBoxFocused(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox { Tag: ParamFieldViewModel field } box)
            box.ItemsSource = field.KeySuggestions(box.Text);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnKeySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (sender.Tag is not ParamFieldViewModel field)
            return;
        field.AddToken(args.ChosenSuggestion as string ?? args.QueryText);
        sender.Text = "";
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

    private void OnPolicyHover(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out var index))
            _viewModel?.PreviewPolicy(index);
    }

    private void OnPolicyLeave(object sender, RoutedEventArgs e) => _viewModel?.PreviewPolicy(null);

    /// <summary>"If it fails, run…": a new step by area, or a step already in the flow (built when it opens).</summary>
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

    private void OnShortcutRecording(object? sender, bool recording) => _viewModel?.SetRecordingShortcut(recording);
}
