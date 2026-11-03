using DazeControl.Editor.ViewModels.Inspector;
using DazeControl.Editor.ViewModels.LibraryPage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DazeControl.Editor.Library;

/// <summary>The Library page (UX §5.1); all logic is in <see cref="LibraryPageViewModel"/>.</summary>
public sealed partial class LibraryView : UserControl
{
    private LibraryPageViewModel? _viewModel;

    public LibraryView()
    {
        InitializeComponent();
    }

    public LibraryPageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            SectionsPivot.ItemsSource = value?.Sections;
            if (value is not null)
                SectionsPivot.SelectedIndex = value.SelectedIndex;
        }
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && SectionsPivot.SelectedIndex >= 0)
            _viewModel.SelectedIndex = SectionsPivot.SelectedIndex;
    }

    private void OnDeviceRowLoaded(object sender, RoutedEventArgs e) => Inspector.InspectorPane.DisableRow(sender);

    private void OnAddClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DeviceOption option)
            _viewModel?.AddCommand.Execute(option);
        // The list sits in the button's flyout: close it once something was added.
        if (sender is FrameworkElement element)
        {
            foreach (var popup in VisualTreeHelperPopups(element))
                popup.IsOpen = false;
        }
    }

    private static IEnumerable<Popup> VisualTreeHelperPopups(FrameworkElement element) =>
        element.XamlRoot is { } root ? Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root) : [];

    private void OnCapture(object sender, RoutedEventArgs e) => _ = _viewModel?.CaptureCommand.ExecuteAsync(null);

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = _viewModel?.RefreshAsync();

    private void OnUsedByOpen(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            FlyoutBase.ShowAttachedFlyout(element);
    }

    private void OnUsedByClick(object sender, ItemClickEventArgs e)
    {
        CloseFlyouts(sender);
        if (e.ClickedItem is FlowLink flow)
            _viewModel?.OpenFlowCommand.Execute(flow);
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LibraryItemViewModel item)
            _ = _viewModel?.OpenLocationCommand.ExecuteAsync(item);
    }

    private void OnRenameSave(object sender, RoutedEventArgs e) => Rename(sender as FrameworkElement);

    private void OnRenameKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        Rename(sender as FrameworkElement);
    }

    // The Save button and the box share a panel in the item's rename flyout; the item rides on their Tag.
    private void Rename(FrameworkElement? element)
    {
        if (element?.Tag is not LibraryItemViewModel item || element.Parent is not Panel panel
            || panel.Children.OfType<TextBox>().FirstOrDefault() is not { } box || _viewModel is null)
            return;
        if (_viewModel.Rename(item, box.Text))
            CloseFlyouts(element);
    }

    private static void CloseFlyouts(object sender)
    {
        if (sender is FrameworkElement element)
        {
            foreach (var popup in VisualTreeHelperPopups(element))
                popup.IsOpen = false;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LibraryItemViewModel item)
            _viewModel?.RemoveCommand.Execute(item);
    }
}
