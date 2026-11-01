using DazeControl.Core.Model;
using DazeControl.Providers.Audio;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class AudioProviderTests
{
    private sealed class FakeAudio : IAudioEndpoints
    {
        public List<string> Calls { get; } = [];

        public AudioEndpoint? Default { get; init; } = new("{default}", "Speakers (Realtek)");

        public Task<AudioEndpoint> WaitForAsync(string match, AudioFlow flow, CancellationToken cancellationToken)
        {
            Calls.Add($"wait {match} {flow}");
            return Task.FromResult(new AudioEndpoint("{" + match + "}", match));
        }

        public Task<AudioEndpoint?> DefaultAsync(AudioFlow flow, CancellationToken cancellationToken) => Task.FromResult(Default);

        public Task SetDefaultAsync(string id, AudioRoles roles, CancellationToken cancellationToken)
        {
            Calls.Add($"default {id} {roles}");
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(string id, float level, CancellationToken cancellationToken)
        {
            Calls.Add($"volume {id} {level:0.00}");
            return Task.CompletedTask;
        }

        public Task SetMuteAsync(string id, bool muted, CancellationToken cancellationToken)
        {
            Calls.Add($"mute {id} {muted}");
            return Task.CompletedTask;
        }
    }

    private static StepContext WithLibrary(StepContext context) => context with
    {
        Library = new Library { Audio = [new AudioDevice("technics", "Technics"), new AudioDevice("mic", "Yeti", AudioFlow.Input)] },
    };

    private static Task Run(IStepProvider provider, params (string, string)[] with) =>
        provider.ExecuteAsync(WithLibrary(Steps.Context(provider.Id, with)), TestContext.Current.CancellationToken);

    [Fact]
    public async Task SetDefaultWaitsForTheDeviceThenSetsAllRoles()
    {
        var audio = new FakeAudio();
        await Run(new AudioSetDefaultProvider(audio), ("device", "technics"));
        Assert.Equal(["wait Technics Output", "default {Technics} All"], audio.Calls);
    }

    [Fact]
    public async Task SetDefaultHonoursRolesAndInputFlow()
    {
        var audio = new FakeAudio();
        await Run(new AudioSetDefaultProvider(audio), ("device", "mic"), ("roles", "communications"));
        Assert.Equal(["wait Yeti Input", "default {Yeti} Communications"], audio.Calls);
    }

    [Fact]
    public async Task VolumeWithoutDeviceUsesTheDefaultOutput()
    {
        var audio = new FakeAudio();
        await Run(new AudioSetVolumeProvider(audio), ("percent", "35"));
        Assert.Equal(["volume {default} 0.35"], audio.Calls);
    }

    [Fact]
    public async Task MuteDefaultsToOn()
    {
        var audio = new FakeAudio();
        await Run(new AudioMuteProvider(audio), ("device", "technics"));
        Assert.Equal(["wait Technics Output", "mute {Technics} True"], audio.Calls);
    }

    [Fact]
    public async Task NoDefaultOutputFailsPlainly()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => Run(new AudioMuteProvider(new FakeAudio { Default = null })));
        Assert.Equal("There's no default output device.", failure.Message);
    }

    [Fact]
    public async Task CoreAudioReadsTheRealDefaultOutput()
    {
        // Read-only against the dev PC's Core Audio: proves the hand-made COM calls work; changes nothing.
        var endpoint = await new CoreAudioEndpoints().DefaultAsync(AudioFlow.Output, TestContext.Current.CancellationToken);
        if (endpoint is not null)
            Assert.False(string.IsNullOrEmpty(endpoint.Id));
    }
}
