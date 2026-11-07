using DazeControl.Editor.ViewModels.BackupCard;
using Microsoft.UI.Xaml.Controls;

namespace DazeControl.Editor.BackupCard;

/// <summary>The content of the restore dialog (DESIGN §6); all logic is in <see cref="RestoreDialogViewModel"/>.</summary>
public sealed partial class RestoreDialogView : UserControl
{
    public RestoreDialogView(RestoreDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public RestoreDialogViewModel ViewModel { get; }
}
