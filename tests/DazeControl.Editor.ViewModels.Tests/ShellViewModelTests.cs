using DazeControl.Editor.ViewModels;

namespace DazeControl.Editor.ViewModels.Tests;

public class ShellViewModelTests
{
    [Fact]
    public void TitleChangeRaisesPropertyChanged()
    {
        var vm = new ShellViewModel();
        string? changed = null;
        vm.PropertyChanged += (_, e) => changed = e.PropertyName;

        vm.Title = "Flows";

        Assert.Equal(nameof(ShellViewModel.Title), changed);
    }
}
