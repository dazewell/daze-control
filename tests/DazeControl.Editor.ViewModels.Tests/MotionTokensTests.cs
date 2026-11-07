namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Shared motion tokens (D-097): off means zero, on means the token.</summary>
public sealed class MotionTokensTests
{
    [Fact]
    public void DisabledAnimationsGiveZeroDuration()
    {
        Assert.Equal(TimeSpan.Zero, MotionTokens.Duration(false, MotionTokens.FadeMs));
    }

    [Fact]
    public void EnabledAnimationsKeepTheTokenAndStayShort()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(MotionTokens.FadeMs), MotionTokens.Duration(true, MotionTokens.FadeMs));
        Assert.InRange(MotionTokens.FadeMs, 100, 200);
        Assert.Equal(TimeSpan.Zero, MotionTokens.Duration(true, 0));
    }
}
