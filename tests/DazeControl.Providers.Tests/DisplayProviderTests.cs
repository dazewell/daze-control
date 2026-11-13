using DazeControl.Core.Model;
using DazeControl.Providers.Display;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class DisplayProviderTests
{
    /// <summary>A well-formed capture of one screen: [1][0], then one zeroed path.</summary>
    private const string OneScreen = "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private sealed class FakeDisplays : IDisplays
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<DisplayOutput> Outputs() =>
            [new("HDMI-1", "LG TV", true, false), new("DP-3", "DELL S2722QC", true, true), new("eDP-0", "", true, true)];

        public void SetTopology(DisplayTopology topology, CancellationToken cancellationToken) => Calls.Add($"topology {topology}");

        public IReadOnlyList<string> UseOnly(IReadOnlyList<string> outputs, CancellationToken cancellationToken)
        {
            Calls.Add($"only {string.Join("+", outputs)}");
            return [.. outputs.Where(o => o == "VGA-9")];
        }

        public DisplayLayout CaptureLayout(string key) => new(key, ["mon"], OneScreen);

        public void ApplyLayout(DisplayLayout layout, CancellationToken cancellationToken) => Calls.Add($"layout {layout.Key}");

        public string? Difference { get; init; }

        public string? LayoutDifferences(DisplayLayout layout)
        {
            Calls.Add($"check {layout.Key}");
            return Difference;
        }
    }

    private static StepContext WithLayout(params (string Name, string Value)[] with) =>
        Steps.Context("display.check", with) with { Library = new Library { Displays = [new DisplayLayout("desk", ["mon"], OneScreen)] } };

    [Fact]
    public async Task CheckPassesWhenTheScreensMatchAndOnlyReads()
    {
        var displays = new FakeDisplays();
        await new DisplayCheckProvider(displays).ExecuteAsync(WithLayout(("layout", "desk")), TestContext.Current.CancellationToken);
        Assert.Equal(["check desk"], displays.Calls);
    }

    [Fact]
    public async Task CheckFailsWithTheDifference()
    {
        var displays = new FakeDisplays { Difference = "Windows set up the screens differently: LG is still off" };
        var failure = await Assert.ThrowsAsync<CheckNotMetException>(() =>
            new DisplayCheckProvider(displays).ExecuteAsync(WithLayout(("layout", "desk")), TestContext.Current.CancellationToken));
        Assert.Contains("LG is still off", failure.Message);
        await new DisplayCheckProvider(displays).ExecuteAsync(WithLayout(("layout", "desk"), ("is", "differs")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CheckDiffersFailsWhenTheyMatchAndUnknownLayoutFails()
    {
        var displays = new FakeDisplays();
        await Assert.ThrowsAsync<CheckNotMetException>(() =>
            new DisplayCheckProvider(displays).ExecuteAsync(WithLayout(("layout", "desk"), ("is", "differs")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<StepFailedException>(() =>
            new DisplayCheckProvider(displays).ExecuteAsync(WithLayout(("layout", "nope")), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("internal", DisplayTopology.Internal)]
    [InlineData("extend", DisplayTopology.Extend)]
    [InlineData("external", DisplayTopology.External)]
    public async Task TopologyMapsTheMode(string mode, DisplayTopology expected)
    {
        var displays = new FakeDisplays();
        await new DisplayTopologyProvider(displays).RunAsync(("mode", mode));
        Assert.Equal([$"topology {expected}"], displays.Calls);
    }

    [Fact]
    public async Task OutputsPassesTheList()
    {
        var displays = new FakeDisplays();
        await new DisplayOutputsProvider(displays).ExecuteAsync(
            Steps.Context("display.outputs", ("outputs", "HDMI-1")), TestContext.Current.CancellationToken);
        Assert.Equal(["only HDMI-1"], displays.Calls);
    }

    [Fact]
    public async Task MissingOutputFailsAndNamesTheConnectedOnes()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => new DisplayOutputsProvider(new FakeDisplays()).RunAsync(("outputs", "VGA-9")));
        Assert.Equal("Not found: VGA-9. Connected: HDMI-1 (LG TV), DP-3 (DELL S2722QC), eDP-0.", failure.Message);
    }

    [Fact]
    public void RealOutputsAreListedReadOnly()
    {
        var outputs = new CcdDisplays(validateOnly: true).Outputs();
        Assert.All(outputs, o => Assert.Matches("^[A-Za-z]+-\\d+(@\\d+)?$", o.Port));
        if (Environment.GetEnvironmentVariable("DAZE_DEV_PC") == "1")
            Assert.Contains(outputs, o => o.Active);
    }

    [Fact]
    public void WindowsAcceptsTheCurrentScreensWithoutApplying()
    {
        // SDC_VALIDATE: Windows checks the request and changes nothing on screen.
        var displays = new CcdDisplays(validateOnly: true);
        var active = displays.Outputs().Where(o => o.Active).Select(o => o.Port).ToList();
        if (active.Count == 0)
            return; // headless CI runner
        Assert.Empty(displays.UseOnly(active, TestContext.Current.CancellationToken));
        if (active.Count >= 2)
            displays.SetTopology(DisplayTopology.Extend, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LayoutResolvesTheLibraryEntry()
    {
        var displays = new FakeDisplays();
        var context = Steps.Context("display.layout", ("layout", "desk")) with
        {
            Library = new Library { Displays = [new DisplayLayout("desk", ["mon"], OneScreen)] },
        };
        await new DisplayLayoutProvider(displays).ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(["layout desk"], displays.Calls);
    }

    [Fact]
    public void DamagedLayoutFailsPlainly()
    {
        var failure = Assert.Throws<StepFailedException>(
            () => new CcdDisplays(validateOnly: true).ApplyLayout(new DisplayLayout("desk", ["mon"], "not base64!"), TestContext.Current.CancellationToken));
        Assert.Equal("The layout \"desk\" is damaged; capture it again.", failure.Message);
    }

    [Fact]
    public void CapturedLayoutIsAcceptedBackWithoutApplying()
    {
        // Spike T4.6 on the dev PC: capture, then SDC_VALIDATE the re-resolved layout; the screens never change.
        var displays = new CcdDisplays(validateOnly: true);
        if (!displays.Outputs().Any(o => o.Active))
            return; // headless CI runner
        var layout = displays.CaptureLayout("now");
        Assert.All(layout.Monitors, m => Assert.Contains("DISPLAY#", m, StringComparison.OrdinalIgnoreCase));
        displays.ApplyLayout(layout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void CancelledStepWritesNothing()
    {
        // The token is checked right before SetDisplayConfig: a stopped step must not switch the screens later.
        var displays = new CcdDisplays(validateOnly: true);
        var active = displays.Outputs().Where(o => o.Active).Select(o => o.Port).ToList();
        if (active.Count == 0)
            return; // headless CI runner
        Assert.Throws<OperationCanceledException>(() => displays.UseOnly(active, new CancellationToken(canceled: true)));
        Assert.Throws<OperationCanceledException>(() => displays.SetTopology(DisplayTopology.Extend, new CancellationToken(canceled: true)));
        Assert.Throws<OperationCanceledException>(
            () => displays.ApplyLayout(displays.CaptureLayout("now"), new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData("HDMI-1", 0, 0, "HDMI-1")]
    [InlineData("HDMI-1", 1, 0, "HDMI-1@2")]
    [InlineData("DP-3", 1, 1, "DP-3")]
    public void SharedPortLabelsAreTellApartByAdapter(string port, int adapter, int first, string expected) =>
        Assert.Equal(expected, CcdDisplays.Disambiguate(port, adapter, first));

    [Fact]
    public void CaptureSizesMatchTheNativeStructs()
    {
        Assert.Equal(DisplayLayout.PathSize, System.Runtime.CompilerServices.Unsafe.SizeOf<Windows.Win32.Devices.Display.DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(DisplayLayout.ModeSize, System.Runtime.CompilerServices.Unsafe.SizeOf<Windows.Win32.Devices.Display.DISPLAYCONFIG_MODE_INFO>());
    }

    [Fact]
    public void LibraryDetailsReadTheNativeFieldsBack()
    {
        // LayoutScreens (Core) reads the capture by byte offset; fill the real structs and check it lands on the fields.
        var path = new Windows.Win32.Devices.Display.DISPLAYCONFIG_PATH_INFO();
        path.sourceInfo.Anonymous.modeInfoIdx = 0;
        path.targetInfo.Anonymous.modeInfoIdx = 0xffffffff;
        path.targetInfo.outputTechnology = Windows.Win32.Devices.Display.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
        path.targetInfo.refreshRate = new() { Numerator = 120000, Denominator = 1000 };
        var mode = new Windows.Win32.Devices.Display.DISPLAYCONFIG_MODE_INFO
        {
            infoType = Windows.Win32.Devices.Display.DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
        };
        mode.Anonymous.sourceMode = new() { width = 3840, height = 2160, position = new() { x = -3840, y = 120 } };
        var bytes = new byte[8 + DisplayLayout.PathSize + DisplayLayout.ModeSize];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 1);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), 1);
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(8), in path);
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(8 + DisplayLayout.PathSize), in mode);

        var screen = Assert.Single(LayoutScreens.Read(new DisplayLayout("d", ["mon"], Convert.ToBase64String(bytes))));

        Assert.Equal(("HDMI", 3840, 2160, -3840, 120, 120.0), (screen.Connection, screen.Width, screen.Height, screen.X, screen.Y, screen.RefreshHz));
    }

    [Fact]
    public void ThisPcsLayoutHasDetailsForEveryScreen()
    {
        var displays = new CcdDisplays(validateOnly: true);
        if (!displays.Outputs().Any(o => o.Active))
            return; // headless CI runner
        var screens = LayoutScreens.Read(displays.CaptureLayout("now"));

        Assert.NotEmpty(screens);
        Assert.All(screens, s => Assert.True(s.Width > 0 && s.Height > 0 && s.RefreshHz > 0, $"{s}"));
        Assert.Contains(screens, s => s.Primary);
    }
}
