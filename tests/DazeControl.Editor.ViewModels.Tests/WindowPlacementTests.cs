using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Where the editor window opens (D-098): saved rectangle, clamped to the monitors that exist now.</summary>
public sealed class WindowPlacementTests : IDisposable
{
    private static readonly MonitorArea Main = new(new PixelRect(0, 0, 1920, 1040), 96);
    private static readonly MonitorArea Side = new(new PixelRect(1920, 0, 2560, 1400), 144);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static WindowSettings Saved(int l, int t, int w, int h, int dpi = 96, bool max = false) =>
        new() { Left = l, Top = t, Width = w, Height = h, Dpi = dpi, Maximized = max };

    [Fact]
    public void NothingSavedGivesTheDefaultSizeCentred()
    {
        var target = WindowPlacementLogic.Resolve(null, [Main], Main);

        Assert.Equal(new PixelRect(410, 160, 1100, 720), target.Bounds);
        Assert.False(target.Maximized);
    }

    [Fact]
    public void DefaultScalesWithDpiAndShrinksToASmallWorkArea()
    {
        var small = new MonitorArea(new PixelRect(0, 0, 1000, 600), 192);

        Assert.Equal(new PixelRect(0, 0, 1000, 600), WindowPlacementLogic.Default(small));
        Assert.Equal(1650, WindowPlacementLogic.Default(Side).Width);
    }

    [Fact]
    public void ASavedRectangleOnAMonitorComesBackAsIs()
    {
        var target = WindowPlacementLogic.Resolve(Saved(100, 50, 1500, 900), [Main], Main);

        Assert.Equal(new PixelRect(100, 50, 1500, 900), target.Bounds);
    }

    [Fact]
    public void TheMaximizedFlagKeepsTheNormalRectangle()
    {
        var target = WindowPlacementLogic.Resolve(Saved(100, 50, 1500, 900, max: true), [Main], Main);

        Assert.True(target.Maximized);
        Assert.Equal(new PixelRect(100, 50, 1500, 900), target.Bounds);
    }

    [Fact]
    public void ARectangleOnAnUnpluggedMonitorFallsBackToTheDefaultOnTheCurrentOne()
    {
        var target = WindowPlacementLogic.Resolve(Saved(2500, 100, 1500, 900), [Main], Main);

        Assert.Equal(WindowPlacementLogic.Default(Main), target.Bounds);
    }

    [Fact]
    public void AMaximizedWindowOnAnUnpluggedMonitorStaysMaximizedOnTheDefault()
    {
        var target = WindowPlacementLogic.Resolve(Saved(2500, 100, 1500, 900, max: true), [Main], Main);

        Assert.True(target.Maximized);
        Assert.Equal(WindowPlacementLogic.Default(Main), target.Bounds);
    }

    [Fact]
    public void OnlyASliverOnScreenCountsAsOffScreen()
    {
        var target = WindowPlacementLogic.Resolve(Saved(1900, 100, 1500, 900), [Main], Main);

        Assert.Equal(WindowPlacementLogic.Default(Main), target.Bounds);
    }

    [Fact]
    public void ALargerWindowAfterAResolutionDropIsClampedIntoTheWorkArea()
    {
        var smaller = new MonitorArea(new PixelRect(0, 0, 1280, 680), 96);

        var target = WindowPlacementLogic.Resolve(Saved(300, 200, 1500, 900), [smaller], smaller);

        Assert.Equal(new PixelRect(0, 0, 1280, 680), target.Bounds);
    }

    [Fact]
    public void AWindowPartlyOffTheEdgeSlidesBackIn()
    {
        var target = WindowPlacementLogic.Resolve(Saved(1000, 600, 1100, 720), [Main], Main);

        Assert.Equal(new PixelRect(820, 320, 1100, 720), target.Bounds);
    }

    [Fact]
    public void ATooSmallSavedSizeFallsBackToTheDefault()
    {
        var target = WindowPlacementLogic.Resolve(Saved(100, 100, 200, 150), [Main], Main);

        Assert.Equal(WindowPlacementLogic.Default(Main), target.Bounds);
    }

    [Fact]
    public void MissingOrNonsenseNumbersFallBackToTheDefault()
    {
        Assert.Equal(WindowPlacementLogic.Default(Main), WindowPlacementLogic.Resolve(new WindowSettings { Left = 5 }, [Main], Main).Bounds);
        Assert.Equal(WindowPlacementLogic.Default(Main), WindowPlacementLogic.Resolve(Saved(0, 0, -5, 900), [Main], Main).Bounds);
        Assert.Equal(WindowPlacementLogic.Default(Main), WindowPlacementLogic.Resolve(Saved(0, 0, 1500, 900), [], Main).Bounds);
    }

    [Fact]
    public void ASizeSavedAtAnotherDpiKeepsItsSizeInDip()
    {
        // 1650x1080 px at 144 dpi = 1100x720 DIP; the 96-dpi main monitor shows it at 1100x720 px.
        var target = WindowPlacementLogic.Resolve(Saved(100, 100, 1650, 1080, dpi: 144), [Main], Main);

        Assert.Equal(new PixelRect(100, 100, 1100, 720), target.Bounds);
    }

    [Fact]
    public void ARectangleOnTheSecondMonitorStaysThereAtItsOwnDpi()
    {
        var target = WindowPlacementLogic.Resolve(Saved(2000, 100, 1650, 1080, dpi: 144), [Main, Side], Main);

        Assert.Equal(new PixelRect(2000, 100, 1650, 1080), target.Bounds);
    }

    [Fact]
    public void ACorruptEditorFileGivesNoSavedWindow()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "editor.json");
        File.WriteAllText(path, "{ \"window\": { \"left\": \"far left\" } ");
        var log = new List<string>();

        using var store = new EditorSettingsStore(path, log: log.Add);

        Assert.Null(store.Settings.Window);
        Assert.Single(log);
        Assert.Equal(WindowPlacementLogic.Default(Main), WindowPlacementLogic.Resolve(store.Settings.Window, [Main], Main).Bounds);
    }

    [Fact]
    public void TheWindowIsWrittenAndReadBack()
    {
        var path = Path.Combine(_folder, "editor.json");
        using (var store = new EditorSettingsStore(path))
            store.Update(s => s.Window = Saved(-30, 40, 1500, 900, dpi: 120, max: true));

        using var again = new EditorSettingsStore(path);

        var window = Assert.IsType<WindowSettings>(again.Settings.Window);
        Assert.Equal((-30, 40, 1500, 900, 120, true), (window.Left, window.Top, window.Width, window.Height, window.Dpi, window.Maximized));
    }
}
