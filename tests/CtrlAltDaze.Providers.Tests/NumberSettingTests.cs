using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

/// <summary>T25.3: a number setting is read after a value was filled in: invariant culture, then the range, in plain sentences.</summary>
public sealed class NumberSettingTests
{
    private static int Read(string text) => Steps.Context("audio.setVolume", ("percent", text)).WholeNumber("percent", 0);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData(" 35 ", 35)]
    [InlineData("42.0", 42)]
    public void AFilledNumberInRangeIsRead(string text, int expected) => Assert.Equal(expected, Read(text));

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("1e9")]
    [InlineData("123456789012345678901234567890")]
    public void OutOfRangeFailsWithASentenceThatDoesNotRepeatTheNumber(string text)
    {
        var failure = Assert.Throws<StepFailedException>(() => Read(text));

        Assert.Equal("Volume (%) must be 0 to 100; the value it uses is outside that.", failure.Message);
    }

    [Theory]
    [InlineData("42.5")]
    [InlineData("42,5")]
    [InlineData("loud")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void NotAWholeNumberFailsWithoutRepeatingTheText(string text)
    {
        var failure = Assert.Throws<StepFailedException>(() => Read(text));

        Assert.Equal("Volume (%) isn't a whole number.", failure.Message);
    }

    [Fact]
    public void AValueThatGaveNothingSaysSo()
    {
        var failure = Assert.Throws<StepFailedException>(() => Read(""));

        Assert.Equal("Volume (%) has no number: the value it uses was empty.", failure.Message);
    }

    [Fact]
    public void ALeftOutSettingUsesItsFallback() =>
        Assert.Equal(7, Steps.Context("audio.setVolume").WholeNumber("percent", 7));
}
