using CtrlAltDaze.Editor.ViewModels.Inspector;
using CtrlAltDaze.Editor.ViewModels.LibraryPage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CtrlAltDaze.Editor.Library;

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
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = value;
            SectionsPivot.ItemsSource = value?.Sections;
            if (value is not null)
            {
                SectionsPivot.SelectedIndex = value.SelectedIndex;
                value.PropertyChanged += OnViewModelChanged;
            }
        }
    }

    /// <summary>The inspector's "Add inputs in the Library" link picks the Android section (L1).</summary>
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryPageViewModel.SelectedIndex) && _viewModel is not null
            && SectionsPivot.SelectedIndex != _viewModel.SelectedIndex)
            SectionsPivot.SelectedIndex = _viewModel.SelectedIndex;
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && SectionsPivot.SelectedIndex >= 0)
            _viewModel.SelectedIndex = SectionsPivot.SelectedIndex;
    }

    private void OnDeviceRowLoaded(object sender, RoutedEventArgs e) => Inspector.InspectorPane.DisableRow(sender);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML wires it on the instance")]
    private void OnDeviceRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e) =>
        Inspector.InspectorPane.DisableRow(sender);

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

    private void OnBindSave(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which rebuilds the list
        if (_viewModel.Bind())
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    private void OnAddByHand(object sender, RoutedEventArgs e) => AddByHand(sender as FrameworkElement);

    private void OnAddByHandKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AddByHand(sender as FrameworkElement);
    }

    private void AddByHand(FrameworkElement? element)
    {
        if (element?.Tag is not LibrarySectionViewModel { Kind: { } kind } || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which rebuilds the list
        if (_viewModel.AddByHand(kind))
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

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

    // ---- Android › Inputs (L1) ----

    private void OnRenameInputSave(object sender, RoutedEventArgs e) => RenameInput(sender as FrameworkElement);

    private void OnRenameInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        RenameInput(sender as FrameworkElement);
    }

    // Like Rename: the button and the box share a panel in the input's flyout; the input rides on their Tag.
    private void RenameInput(FrameworkElement? element)
    {
        if (element?.Tag is not AndroidInputViewModel input || BoxBeside(element) is not { } box || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which can rebuild the row
        if (_viewModel.RenameInput(input, box.Text))
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    private void OnAddInputSave(object sender, RoutedEventArgs e) => AddInput(sender as FrameworkElement);

    private void OnAddInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AddInput(sender as FrameworkElement);
    }

    private void AddInput(FrameworkElement? element)
    {
        if (element?.Tag is not LibraryItemViewModel item || BoxBeside(element) is not { } box || _viewModel is null)
            return;
        var popups = VisualTreeHelperPopups(element).ToList(); // before the edit, which can rebuild the row
        if (_viewModel.AddInput(item, box.Text))
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
        }
    }

    private static TextBox? BoxBeside(FrameworkElement element) =>
        (element.Parent as Panel)?.Children.OfType<TextBox>().FirstOrDefault();

    private void OnRemoveInput(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AndroidInputViewModel input)
            _viewModel?.RemoveInputCommand.Execute(input);
    }

    private void OnAddUp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AndroidInputViewModel input)
            _viewModel?.AddUpCommand.Execute(input);
    }

    private void OnAddDown(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AndroidInputViewModel input)
            _viewModel?.AddDownCommand.Execute(input);
    }

    private void OnRemoveMove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MoveToken move)
            _viewModel?.RemoveMoveCommand.Execute(move);
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
