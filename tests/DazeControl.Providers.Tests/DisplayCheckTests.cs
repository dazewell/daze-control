using DazeControl.Providers.Display;

namespace DazeControl.Providers.Tests;

/// <summary>
/// The user's PC (PLAN F5): one adapter, the laptop panel (target 0x800050) and a DELL on HDMI (0x9038), each offered
/// on sources 0 and 1. Their Extend: DELL primary at 0,0, laptop left at −2880.
/// </summary>
public class DisplayCheckTests
{
    private const long Gpu = 0xeeb7;
    private const uint Int = 0x800050, Dell = 0x9038;

    private static readonly CcdPath[] Offered =
        [new(Gpu, 0, Int), new(Gpu, 1, Int), new(Gpu, 0, Dell), new(Gpu, 1, Dell)];

    [Fact]
    public void EachScreenGetsItsOwnSourceEvenWhenBothHaveSourceZero()
    {
        // From the old duplicate state both paths are on source 0: "Use only" both must split them.
        var plan = DisplayCheck.AssignSources([new(Gpu, Int, 0, 0), new(Gpu, Dell, 1, 0)], Offered);
        Assert.Equal([0u, 1u], plan!);
    }

    [Fact]
    public void ScreensKeepTheSourceTheyHave()
    {
        var plan = DisplayCheck.AssignSources([new(Gpu, Int, 0, null), new(Gpu, Dell, 1, 1)], Offered);
        Assert.Equal([0u, 1u], plan!);
    }

    [Fact]
    public void ALayoutsSavedSourcesAreKeptWhenFree()
    {
        // Dual was captured with the laptop on source 1 and the DELL on source 0.
        var plan = DisplayCheck.AssignSources([new(Gpu, Int, 0, 1), new(Gpu, Dell, 1, 0)], Offered);
        Assert.Equal([1u, 0u], plan!);
    }

    [Fact]
    public void ASavedSourceTheAdapterDoesntOfferIsReplaced()
    {
        var plan = DisplayCheck.AssignSources([new(Gpu, Int, 0, 7), new(Gpu, Dell, 1, 0)], Offered);
        Assert.Equal([1u, 0u], plan!);
    }

    [Fact]
    public void AClonedGroupSharesOneSource()
    {
        var plan = DisplayCheck.AssignSources([new(Gpu, Int, 0, 0), new(Gpu, Dell, 0, 0)], Offered);
        Assert.Equal([0u, 0u], plan!);
    }

    [Fact]
    public void APreferenceGivesWayWhenOnlyTheOtherOrderWorks()
    {
        // Copilot review: A prefers 0 (offered 0 and 1), B prefers 1 but is offered only 0.
        CcdPath[] uneven = [new(Gpu, 0, Int), new(Gpu, 1, Int), new(Gpu, 0, Dell)];
        Assert.Equal([1u, 0u], DisplayCheck.AssignSources([new(Gpu, Int, 0, 0), new(Gpu, Dell, 1, 1)], uneven)!);
    }

    [Fact]
    public void TooFewSourcesGivesNoPlan()
    {
        CcdPath[] oneSource = [new(Gpu, 0, Int), new(Gpu, 0, Dell)];
        Assert.Null(DisplayCheck.AssignSources([new(Gpu, Int, 0, 0), new(Gpu, Dell, 1, 0)], oneSource));
    }

    [Fact]
    public void SourcesCountPerAdapter()
    {
        const long Other = 0x1234;
        CcdPath[] twoGpus = [new(Gpu, 0, Int), new(Other, 0, Dell)];
        Assert.Equal([0u, 0u], DisplayCheck.AssignSources([new(Gpu, Int, 0, null), new(Other, Dell, 1, null)], twoGpus)!);
    }

    private const string IntPath = @"\\?\DISPLAY#SDC422A#4&17058f2d&0&UID8388688", DellPath = @"\\?\DISPLAY#DELA1CF#4&17058f2d&0&UID36920";

    private static ScreenState Laptop(uint source = 0, int x = -2880) => new("ATNA40HQ05-0", IntPath, Gpu, source, 2880, 1800, x, 0);

    private static ScreenState DellNow(uint source = 1, int x = 0) => new("DELL S2722QC", DellPath, Gpu, source, 3840, 2160, x, 0);

    private static readonly ScreenWant WantLaptop = new("ATNA40HQ05-0", IntPath, 0, (2880, 1800, -2880, 0));
    private static readonly ScreenWant WantDell = new("DELL S2722QC", DellPath, 1, (3840, 2160, 0, 0));

    [Fact]
    public void TheUsersExtendMatchesDual() =>
        Assert.Null(DisplayCheck.Differences([WantLaptop, WantDell], [Laptop(), DellNow()], "Dual"));

    [Fact]
    public void AScreenLeftOffIsNamed() =>
        Assert.Equal("Windows set up the screens differently: DELL S2722QC is still off.",
            DisplayCheck.Differences([WantLaptop, WantDell], [Laptop()]));

    [Fact]
    public void UseOnlyTheDellFromExtendThatLeftTheLaptopDuplicatedFails()
    {
        // X-009: two paths, one source, and the step said Succeeded.
        Assert.Equal("Windows set up the screens differently: ATNA40HQ05-0 is still on.",
            DisplayCheck.Differences([WantDell with { Group = 0 }], [Laptop(0, 0), DellNow(0)]));
    }

    [Fact]
    public void TwoScreensOnOnePictureFailUnlessAsked()
    {
        Assert.Equal("Windows set up the screens differently: ATNA40HQ05-0 and DELL S2722QC show the same picture.",
            DisplayCheck.Differences([WantLaptop, WantDell], [Laptop(0, 0), DellNow(0)]));
        Assert.Null(DisplayCheck.Differences([WantLaptop with { Group = 1 }, WantDell], [Laptop(0, 0), DellNow(0)]));
    }

    [Fact]
    public void ASavedDuplicateThatCameUpApartFails() =>
        Assert.Equal("Windows set up the screens differently: ATNA40HQ05-0 and DELL S2722QC don't show the same picture.",
            DisplayCheck.Differences([WantLaptop with { Group = 1, Place = null }, WantDell with { Place = null }], [Laptop(0), DellNow(1)], "Mirror"));

    [Fact]
    public void AMadeUpArrangementFailsALayout()
    {
        // X-009: laptop primary, DELL to its right.
        Assert.Equal("Windows set up the screens differently: ATNA40HQ05-0 isn't where Dual puts it.",
            DisplayCheck.Differences([WantLaptop, WantDell], [Laptop(0, 0), DellNow(1, 2880)], "Dual"));
    }

    [Fact]
    public void ScreensMatchByMonitorNotTargetId() =>
        // Live matrix: from Duplicate the DELL was offered as target 0x1009038 and came back as 0x9038.
        Assert.Null(DisplayCheck.Differences([WantLaptop, WantDell with { Monitor = DellPath.ToUpperInvariant() }], [Laptop(), DellNow()], "Dual"));

    [Fact]
    public void UseOnlyDoesntCheckPositions() =>
        Assert.Null(DisplayCheck.Differences([WantLaptop with { Place = null }, WantDell with { Place = null }], [Laptop(0, 0), DellNow(1, 2880)]));
}
