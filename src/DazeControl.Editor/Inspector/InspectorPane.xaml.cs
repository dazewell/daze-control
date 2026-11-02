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

    private void OnDevicesOpened(object? sender, object e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamFieldViewModel { Spec.Library: { } kind })
            _ = _viewModel?.RefreshDevicesAsync(kind);
    }

    private void OnPolicyHover(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out var index))
            _viewModel?.PreviewPolicy(index);
    }

    private void OnPolicyLeave(object sender, RoutedEventArgs e) => _viewModel?.PreviewPolicy(null);

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
}
