using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Providers.Audio;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

/// <summary>T24.5 (D-138): <c>audio.media</c> behind a fake sink; no test sends a real media key.</summary>
public class AudioMediaTests
{
    private sealed class FakeMedia : IMediaCommands
    {
        public List<MediaCommand> Sent { get; } = [];
        public string? Fail { get; init; }

        public void Send(MediaCommand command)
        {
            if (Fail is not null)
                throw new StepFailedException(Fail);
            Sent.Add(command);
        }
    }

    [Theory]
    [InlineData("playPause", MediaCommand.PlayPause)]
    [InlineData("play", MediaCommand.Play)]
    [InlineData("pause", MediaCommand.Pause)]
    [InlineData("stop", MediaCommand.Stop)]
    [InlineData("next", MediaCommand.NextTrack)]
    [InlineData("previous", MediaCommand.PreviousTrack)]
    public async Task EachActionSendsItsCommand(string action, MediaCommand expected)
    {
        var media = new FakeMedia();
        await new AudioMediaProvider(media).RunAsync(("action", action));
        Assert.Equal([expected], media.Sent);
    }

    [Fact]
    public async Task ItsDefaultActionIsPlayOrPause()
    {
        var media = new FakeMedia();
        var provider = new AudioMediaProvider(media);
        await provider.ExecuteAsync(Steps.Context("audio.media"), TestContext.Current.CancellationToken);
        Assert.Equal([MediaCommand.PlayPause], media.Sent);
    }

    [Theory]
    [InlineData("rewind")]
    [InlineData("Play")]
    [InlineData(" play")]
    public async Task AnUnknownActionFailsWithASentenceAndSendsNothing(string action)
    {
        var media = new FakeMedia();
        var e = await Assert.ThrowsAsync<StepFailedException>(() => new AudioMediaProvider(media).RunAsync(("action", action)));
        Assert.Contains("isn't a playback action", e.Message, StringComparison.Ordinal);
        Assert.Empty(media.Sent);
    }

    [Fact]
    public async Task ACancelledRunSendsNothing()
    {
        var media = new FakeMedia();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioMediaProvider(media).ExecuteAsync(Steps.Context("audio.media", ("action", "play")), cts.Token));
        Assert.Empty(media.Sent);
    }

    [Fact]
    public async Task AFailureFromTheSinkIsTheStepsFailure()
    {
        var e = await Assert.ThrowsAsync<StepFailedException>(() =>
            new AudioMediaProvider(new FakeMedia { Fail = "no taskbar" }).RunAsync(("action", "stop")));
        Assert.Equal("no taskbar", e.Message);
    }

    [Fact]
    public void TheCatalogAndTheProviderAgreeAndCaptionsMatchAndroid()
    {
        var media = ProviderCatalog.All.Single(d => d.Id == "audio.media").FindParam("action")!;
        var android = ProviderCatalog.All.Single(d => d.Id == "android.media").FindParam("action")!;

        Assert.Equal(AudioMediaProvider.Commands.Keys.Order(), media.Choices.Order());
        foreach (var (choice, caption) in media.Choices.Zip(media.Captions))
            Assert.Equal(caption, android.Captions[android.Choices.ToList().IndexOf(choice)]);
    }
}
