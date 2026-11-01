using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core;

namespace DazeControl.Editor.ViewModels;

/// <summary>Editor window state; pages arrive in M6.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = AppInfo.ProductName;
}
