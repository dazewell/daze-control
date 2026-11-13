using System.ComponentModel;
using CtrlAltDaze.Core.Backup;
using CtrlAltDaze.Editor.ViewModels.BackupCard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CtrlAltDaze.Editor.BackupCard;

/// <summary>Settings › Backup and restore (T19.4): the pickers, drag-drop and dialogs; the rules are in <see cref="BackupCardViewModel"/>.</summary>
public sealed partial class BackupCardView : UserControl
{
    private BackupCardViewModel? _viewModel;
    private bool _asking;

    public BackupCardView()
    {
        InitializeComponent();
    }

    /// <summary>Set once by the page; null (a window without a backup service) hides the card.</summary>
    public BackupCardViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is { } old)
                old.PropertyChanged -= OnViewModelChanged;
            _viewModel = value;
            Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            if (value is null)
                return;
            value.PropertyChanged += OnViewModelChanged;
            value.Refresh();
            Bindings.Update();
            ShowSeverity();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupCardViewModel.MessageKind))
            ShowSeverity();
    }

    private void ShowSeverity() => MessageBar.Severity = _viewModel?.MessageKind switch
    {
        BackupMessageKind.Success => InfoBarSeverity.Success,
        BackupMessageKind.Warning => InfoBarSeverity.Warning,
        BackupMessageKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => _viewModel?.Dismiss();

    private static IntPtr Hwnd => WindowNative.GetWindowHandle(App.Instance!.MainWindow!);

    private async void OnBackUpNow(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_viewModel.SuggestedFileName),
        };
        picker.FileTypeChoices.Add("Ctrl Alt Daze backup", [BackupFormat.Extension]);
        InitializeWithWindow.Initialize(picker, Hwnd);
        // The picker leaves an empty placeholder: remove only that. A file with content is an earlier backup the user picked; it stays
        // until the new one is written, so the service adds a counter to the name instead of overwriting it.
        if (await picker.PickSaveFileAsync() is not { } file)
            return;
        var path = file.Path;
        try
        {
            if (new FileInfo(path).Length == 0)
                await file.DeleteAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Left in place: the service then adds a counter to the name.
        }
        _viewModel.BackupNow(path);
    }

    private async void OnRestoreFromFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(BackupFormat.Extension);
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, Hwnd);
        if (await picker.PickSingleFileAsync() is { } file)
            await RestoreAsync(file.Path, earlier: false);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Restore from this file";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var deferral = e.GetDeferral();
        try
        {
            if ((await e.DataView.GetStorageItemsAsync()) is [StorageFile file, ..])
                await RestoreAsync(file.Path, earlier: false);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnRestoreRow(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is BackupRowViewModel row)
            await RestoreAsync(row.Path, row.IsEarlier);
    }

    private async void OnDeleteRow(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not BackupRowViewModel row || _viewModel is null || _asking)
            return;
        _asking = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = row.IsEarlier ? "Delete this earlier save?" : "Delete this backup?",
                Content = $"{row.Title}, {row.Subtitle}. This can't be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Keep",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                _viewModel.Delete(row);
        }
        finally
        {
            _asking = false;
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _viewModel?.Undo();

    private async Task RestoreAsync(string path, bool earlier)
    {
        if (_viewModel is null || _asking)
            return;
        _asking = true;
        try
        {
            if (_viewModel.OpenForRestore(path, earlier) is not { } model)
                return;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Restore from backup",
                Content = new RestoreDialogView(model),
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close, // the safer answer is focused (DESIGN §9)
                IsPrimaryButtonEnabled = model.CanRestore,
            };
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RestoreDialogViewModel.CanRestore))
                    dialog.IsPrimaryButtonEnabled = model.CanRestore;
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                model.Apply();
        }
        finally
        {
            _asking = false;
        }
    }
}
