using DazeControl.Core.Layout;

namespace DazeControl.Core.Tests;

public class ScreenMatchTests
{
    private static readonly (string Port, string Monitor)[] Screens =
        [("DP-1", "DELL U2723QE HDR"), ("DP-2", "DELL U2723QE"), ("HDMI-1", "")];

    [Theory]
    [InlineData("DELL U2723QE", 1)] // exact before "contains" (D-051)
    [InlineData("dell u2723qe hdr", 0)]
    [InlineData("hdmi-1", 2)]
    [InlineData("U2723QE", 0)] // no exact name: the first that contains it, as before
    [InlineData("LG", -1)]
    public void AnEntryMeansItsPortOrExactNameFirst(string entry, int index) =>
        Assert.Equal(index, ScreenMatch.IndexOf(entry, Screens, s => s.Port, s => s.Monitor));
}
