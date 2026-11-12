namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>
/// The run-result InfoBar sits over the canvas. InfoBar fills are translucent, so it needs an opaque host, and it must
/// not cover the palette search or the inspector (the "Stopped" banner drawn over "Search steps").
/// </summary>
public sealed class XamlRunInfoOverlayTests
{
    [Fact]
    public void RunInfoIsInAnOpaqueHostClearOfPaletteAndInspector()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "DazeControl.Editor")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!, "src", "DazeControl.Editor", "ShellWindow.xaml"));
        var start = xaml.IndexOf("x:Name=\"RunInfoHost\"", StringComparison.Ordinal);
        Assert.True(start > 0, "RunInfoHost missing");
        var host = xaml[start..xaml.IndexOf("</Border>", start, StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"RunInfo\"", host);
        Assert.Contains("Background=", host);
        Assert.Contains("BorderBrush=", host);
        Assert.Contains("Margin=\"252,8,332,0\"", host); // 240 palette + 12, 320 inspector + 12
    }
}
