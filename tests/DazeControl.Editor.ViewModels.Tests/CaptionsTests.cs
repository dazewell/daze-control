using DazeControl.Editor.ViewModels;
using Xunit;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class CaptionsTests
{
    [Theory]
    [InlineData("a", "b", "c", "a · b · c")]
    [InlineData("", "b", "c", "b · c")]
    [InlineData("a", "", "c", "a · c")]
    [InlineData("a", "b", "", "a · b")]
    [InlineData("a", "", "", "a")]
    [InlineData("", "b", "", "b")]
    [InlineData("", "", "c", "c")]
    [InlineData("", "", "", "")]
    public void AbsentPartsLeaveNoSeparator(string a, string b, string c, string expected) =>
        Assert.Equal(expected, Captions.Join(a, b, c));

    [Fact]
    public void NullAndBlankPartsAreDropped() =>
        Assert.Equal("x · y", Captions.Join(null, "x", "  ", "y"));

    [Fact]
    public void SpacesInsideAPartStay() => Assert.Equal("No shortcut · 1 step", Captions.Join("No shortcut", "1 step"));
}
