namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Guards for the narrow-window layout (F-005, F-014, F-023): the pieces the code-behind rearranges must stay named.</summary>
public sealed class XamlResponsiveLayoutTests
{
    private static string Src(string file)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "DazeControl.Editor")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "DazeControl.Editor", file));
    }

    [Theory]
    [InlineData("PageGrid")]
    [InlineData("FlowCommandBar")]
    [InlineData("FlowSearchBox")]
    [InlineData("FlowSortBox")]
    [InlineData("FlowShowBox")]
    [InlineData("FlowOrderButton")]
    [InlineData("FlowSelectToggle")]
    [InlineData("PalettePane")]
    [InlineData("InspectorPaneControl")]
    public void ResponsiveElementsAreNamed(string name) =>
        Assert.Contains($"x:Name=\"{name}\"", Src("ShellWindow.xaml"));

    [Fact]
    public void CommandBarHasRowsForTheNarrowLayout()
    {
        var xaml = Src("ShellWindow.xaml");
        var start = xaml.IndexOf("x:Name=\"FlowCommandBar\"", StringComparison.Ordinal);
        var bar = xaml[start..xaml.IndexOf("</Grid.RowDefinitions>", start, StringComparison.Ordinal)];
        Assert.Equal(3, bar.Split("<RowDefinition").Length - 1);
    }

    [Fact]
    public void CardCaptionTrimsWithEllipsisInsteadOfClipping()
    {
        var xaml = Src("ShellWindow.xaml");
        Assert.Contains("Text=\"{x:Bind CaptionLead, Mode=OneWay}\" Style=\"{StaticResource SettingsCardDescription}\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" MaxWidth=\"200\"", xaml);
    }

    [Fact]
    public void WindowHasAMinimumSizeAndDragGhostIsHidden()
    {
        Assert.Contains("PreferredMinimumWidth", Src("WindowPlacementInterop.cs"));
        Assert.Contains("IsContentVisible = false", Src("ShellWindow.xaml.cs"));
    }
}
