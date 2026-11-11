using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Providers.Audio;
using DazeControl.Providers.Basic;
using DazeControl.Providers.Files;
using DazeControl.Providers.Process;
using DazeControl.Providers.Service;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

/// <summary>Facts and check steps (T14.8, D-081): read-only, fakes only; the real file and process probes read this test host.</summary>
public class CheckTests
{
    private sealed class FakeAudio(string? defaultName, AudioEndpoint?[]? roleDefaults = null) : IAudioEndpoints
    {
        public Task<AudioEndpoint?> DefaultAsync(AudioFlow flow, CancellationToken cancellationToken) =>
            Task.FromResult(defaultName is null ? null : new AudioEndpoint("{d}", defaultName));

        public Task<AudioEndpoint> WaitForAsync(string match, AudioFlow flow, CancellationToken cancellationToken) => throw new InvalidOperationException("a check never waits or changes");

        public Task<IReadOnlyList<AudioEndpoint?>> DefaultsAsync(AudioFlow flow, AudioRoles roles, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioEndpoint?>>(roleDefaults ?? [defaultName is null ? null : new AudioEndpoint("{d}", defaultName)]);

        public Task SetDefaultAsync(string id, AudioRoles roles, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task SetVolumeAsync(string id, float level, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task SetMuteAsync(string id, bool muted, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class FakeProcesses(params string[] running) : IProcesses
    {
        public bool IsRunning(string name) => running.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeServices(ServiceState state) : IServices
    {
        public ServiceState Query(string name) => state;

        public void RequestStart(string name, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public void RequestStop(string name, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private static StepContext WithAudio(StepContext context) =>
        context with { Library = Library.Empty with { Audio = [new AudioDevice("tv", "Samsung")] } };

    private sealed class NoNotifier : INotifier
    {
        public void Show(string flowName, string text)
        {
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Samsung HDMI", true)]
    [InlineData("Speakers (Realtek)", false)]
    public async Task AudioFactIsWhetherTheDeviceIsTheDefault(string current, bool expected)
    {
        var reading = await new AudioDefaultProbe(new FakeAudio(current)).ReadAsync(WithAudio(Steps.Context("audio.setDefault", ("device", "tv"))), Token);
        Assert.Equal(expected, reading.Value);
        Assert.Equal(expected ? "tv is the default output" : "tv isn't the default output", reading.Text);
    }

    [Fact]
    public async Task AudioFactNeedsEveryRoleTheStepWouldSet()
    {
        var tv = new AudioEndpoint("{tv}", "Samsung HDMI");
        var other = new AudioEndpoint("{o}", "Speakers");
        var probe = new AudioDefaultProbe(new FakeAudio(null, [tv, tv, other]));
        // One role still points elsewhere: not yet the default, so the setter must still run.
        Assert.False((await probe.ReadAsync(WithAudio(Steps.Context("audio.setDefault", ("device", "tv"))), Token)).Value);
        Assert.True((await new AudioDefaultProbe(new FakeAudio(null, [tv, tv, tv])).ReadAsync(WithAudio(Steps.Context("audio.setDefault", ("device", "tv"))), Token)).Value);
    }

    [Fact]
    public async Task AudioCheckFailsWithTheProbesSentence()
    {
        var check = new FactCheckProvider("audio.check", new AudioDefaultProbe(new FakeAudio("Speakers")), "default");
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => check.ExecuteAsync(WithAudio(Steps.Context("audio.check", ("device", "tv"))), Token));
        Assert.Equal("tv isn't the default output", failure.Message);
        await check.ExecuteAsync(WithAudio(Steps.Context("audio.check", ("device", "tv"), ("is", "notDefault"))), Token);
    }

    [Fact]
    public async Task AudioCheckWithoutADeviceIsAFailureNotACrash()
    {
        var probe = new AudioDefaultProbe(new FakeAudio(null));
        await Assert.ThrowsAsync<StepFailedException>(() => probe.ReadAsync(Steps.Context("audio.check"), Token));
    }

    [Theory]
    [InlineData(@"C:\Apps\Spotify.exe", "Spotify")]
    [InlineData("\"C:\\Apps\\Spotify.EXE\"", "Spotify")]
    [InlineData("spotify", "spotify")]
    public void ProgramNameDropsFolderAndExe(string text, string expected) =>
        Assert.Equal(expected, ProcessRunningProbe.ProgramName(text));

    [Fact]
    public async Task ProcessFactReadsTheRunStepsPathAndTheChecksProgram()
    {
        var probe = new ProcessRunningProbe(new FakeProcesses("spotify"));
        Assert.True((await probe.ReadAsync(Steps.Context("process.run", ("path", @"C:\x\Spotify.exe")), Token)).Value);
        var check = await probe.ReadAsync(Steps.Context("process.check", ("program", "notepad")), Token);
        Assert.False(check.Value);
        Assert.Equal("notepad isn't running", check.Text);
    }

    [Fact]
    public async Task ProcessCheckFailsWhenNotRunningAndNotRunningIsAskedFor()
    {
        var check = new FactCheckProvider("process.check", new ProcessRunningProbe(new FakeProcesses()), "running");
        await Assert.ThrowsAsync<StepFailedException>(() => check.ExecuteAsync(Steps.Context("process.check", ("program", "x")), Token));
        await check.ExecuteAsync(Steps.Context("process.check", ("program", "x"), ("is", "notRunning")), Token);
    }

    [Fact]
    public async Task TheRealProcessProbeSeesThisTestHost()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        Assert.True((await new ProcessRunningProbe(new WindowsProcesses()).ReadAsync(Steps.Context("process.check", ("program", self)), Token)).Value);
    }

    [Theory]
    [InlineData(ServiceState.Running, true)]
    [InlineData(ServiceState.Stopped, false)]
    public async Task ServiceFactIsRunning(ServiceState state, bool expected) =>
        Assert.Equal(expected, (await new ServiceRunningProbe(new FakeServices(state)).ReadAsync(Steps.Context("service.check", ("name", "x")), Token)).Value);

    [Theory]
    [InlineData(ServiceState.Pending)]
    [InlineData(ServiceState.Paused)]
    public async Task ServiceInBetweenIsNoAnswer(ServiceState state) =>
        await Assert.ThrowsAsync<StepFailedException>(() => new ServiceRunningProbe(new FakeServices(state)).ReadAsync(Steps.Context("service.check", ("name", "x")), Token));

    [Fact]
    public async Task ServiceCheckStoppedPassesOnAStoppedService()
    {
        var check = new FactCheckProvider("service.check", new ServiceRunningProbe(new FakeServices(ServiceState.Stopped)), "running");
        await Assert.ThrowsAsync<StepFailedException>(() => check.ExecuteAsync(Steps.Context("service.check", ("name", "x")), Token));
        await check.ExecuteAsync(Steps.Context("service.check", ("name", "x"), ("is", "stopped")), Token);
    }

    [Fact]
    public async Task FileCheckExistsAndMissingOnATempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        var check = new FactCheckProvider("file.check", new FileExistsProbe(), "exists");
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => check.ExecuteAsync(Steps.Context("file.check", ("path", path)), Token));
        Assert.EndsWith("doesn't exist", failure.Message);
        await check.ExecuteAsync(Steps.Context("file.check", ("path", path), ("is", "missing")), Token);

        await File.WriteAllTextAsync(path, "x", Token);
        try
        {
            await check.ExecuteAsync(Steps.Context("file.check", ("path", path)), Token);
            await Assert.ThrowsAsync<StepFailedException>(() => check.ExecuteAsync(Steps.Context("file.check", ("path", path), ("is", "missing")), Token));
        }
        finally
        {
            File.Delete(path);
        }
        await check.ExecuteAsync(Steps.Context("file.check", ("path", Path.GetTempPath()), ("is", "exists")), Token);
    }

    [Fact]
    public void EveryConditionHasAProbeAndEveryCheckOneProvider()
    {
        var probes = BuiltInProviders.Probes().Select(p => p.Fact).ToList();
        Assert.Equal(probes.Count, probes.Distinct().Count());
        foreach (var condition in ProviderCatalog.All.SelectMany(d => d.Conditions))
            Assert.Contains(condition.Fact, probes);
        var providers = BuiltInProviders.Create(TimeProvider.System, new NoNotifier()).Select(p => p.Id).ToList();
        foreach (var id in BuiltInProviders.StateChecks.Select(c => c.Step).Concat(["audio.check", "process.check", "service.check", "file.check"]))
            Assert.Single(providers, id);
    }

    [Fact]
    public void ActionStepsGetTheNotedConditions()
    {
        Assert.Equal(["notDefault"], ProviderCatalog.Find("audio.setDefault")!.Conditions.Select(c => c.Id));
        Assert.Equal(["notRunning"], ProviderCatalog.Find("process.run")!.Conditions.Select(c => c.Id));
        Assert.Equal(["stopped"], ProviderCatalog.Find("service.start")!.Conditions.Select(c => c.Id));
        Assert.Equal(["running"], ProviderCatalog.Find("service.stop")!.Conditions.Select(c => c.Id));
    }
}
