using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>M22 batch 4: accessible names and Tab order, guarded in the XAML text and the view-model records.</summary>
public sealed class XamlAccessibilityTests
{
    private static string Shell()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "DazeControl.Editor")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "DazeControl.Editor", "ShellWindow.xaml"));
    }

    [Fact]
    public void PaletteGroupIsNamedByItsAreaNotTheRecordDump()
    {
        var group = new PaletteGroup("Audio", []);
        Assert.Equal("Audio", group.ToString());
        Assert.Contains("AutomationProperties.Name=\"{x:Bind Area}\"", Shell());
    }

    [Fact]
    public void EditorTabOrderIsPaletteThenCanvasThenInspector()
    {
        var xaml = Shell();
        Assert.Contains("x:Name=\"PalettePane\" TabIndex=\"1\"", xaml);
        Assert.Contains("<Border Grid.Column=\"1\" TabIndex=\"2\"", xaml);
        Assert.Contains("Width=\"320\" TabIndex=\"3\"", xaml);
    }

    [Fact]
    public void UsedByChipAndMoveItemsSayWhatTheyDo()
    {
        var xaml = Shell();
        Assert.Contains("AutomationProperties.Name=\"{x:Bind UsedBy, Mode=OneWay}\"", xaml);
        Assert.Contains("KeyboardAcceleratorTextOverride=\"Alt+Up\"", xaml);
        Assert.Contains("KeyboardAcceleratorTextOverride=\"Alt+Down\"", xaml);
    }
}
