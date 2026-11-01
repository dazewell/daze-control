using DazeControl.Core.Model;
using DazeControl.Providers.Display;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class DisplayProviderTests
{
    private sealed class FakeDisplays : IDisplays
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<DisplayOutput> Outputs() =>
            [new("HDMI-1", "LG TV", true, false), new("DP-3", "DELL S2722QC", true, true), new("eDP-0", "", true, true)];

        public void SetTopology(DisplayTopology topology) => Calls.Add($"topology {topology}");

        public IReadOnlyList<string> UseOnly(IReadOnlyList<string> outputs)
        {
            Calls.Add($"only {string.Join("+", outputs)}");
            return [.. outputs.Where(o => o == "VGA-9")];
        }

        public DisplayLayout CaptureLayout(string key) => new(key, ["mon"], "AAAA");

        public void ApplyLayout(DisplayLayout layout) => Calls.Add($"layout {layout.Key}");
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
        Assert.All(outputs, o => Assert.Matches("^[A-Za-z]+-\\d+$", o.Port));
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
        Assert.Empty(displays.UseOnly(active));
        if (active.Count >= 2)
            displays.SetTopology(DisplayTopology.Extend);
    }

    [Fact]
    public async Task LayoutResolvesTheLibraryEntry()
    {
        var displays = new FakeDisplays();
        var context = Steps.Context("display.layout", ("layout", "desk")) with
        {
            Library = new Library { Displays = [new DisplayLayout("desk", ["mon"], "AAAA")] },
        };
        await new DisplayLayoutProvider(displays).ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(["layout desk"], displays.Calls);
    }

    [Fact]
    public void DamagedLayoutFailsPlainly()
    {
        var failure = Assert.Throws<StepFailedException>(
            () => new CcdDisplays(validateOnly: true).ApplyLayout(new DisplayLayout("desk", ["mon"], "not base64!")));
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
        displays.ApplyLayout(layout);
    }
}
