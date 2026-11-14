using CtrlAltDaze.Editor.ViewModels.Canvas;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>M22 batch 4: accessible names and Tab order, guarded in the XAML text and the view-model records.</summary>
public sealed class XamlAccessibilityTests
{
    private static string Shell()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "CtrlAltDaze.Editor")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "CtrlAltDaze.Editor", "ShellWindow.xaml"));
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

    [Theory]
    [InlineData("ms-settings:colors")]
    [InlineData("ms-settings:easeofaccess-display")]
    [InlineData("ms-settings:easeofaccess-visualeffects")]
    public void SettingsLinksToWindowsAppearance(string uri)
    {
        var xaml = Shell();
        Assert.Contains($"Tag=\"{uri}\" Click=\"OnOpenWindowsSettings\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Change the theme in Windows settings\"", xaml);
    }

    /// <summary>F-024: WinUI's light tokens (ControlStrongStroke #9C000000, CardStroke #0F000000) over a white card.</summary>
    [Fact]
    public void LightOutlineMeetsThreeToOneButDefaultCardStrokeDoesNot()
    {
        Assert.True(Contrast(0x9C, 0xFF) >= 3.0, "strong stroke");
        Assert.True(Contrast(0x0F, 0xFF) < 3.0, "default card stroke is why light mode needs the strong one");
    }

    // Black at `alpha` over white, against white.
    private static double Contrast(int alpha, int back)
    {
        double a = alpha / 255.0, fg = (1 - a) * 255 / 255.0;
        static double L(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        double l1 = L(1.0), l2 = L(fg);
        return (l1 + 0.05) / (l2 + 0.05);
    }
}
