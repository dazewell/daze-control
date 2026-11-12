using System.Diagnostics;
using DazeControl.Core.Catalog;
using DazeControl.Providers.Basic;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class BasicProviderTests
{
    private sealed class RecordingNotifier : INotifier
    {
        public List<(string Flow, string Text, bool Sound)> Shown { get; } = [];

        public void Show(string flowName, string text, bool sound) => Shown.Add((flowName, text, sound));
    }

    [Fact]
    public async Task WaitLastsTheDuration()
    {
        var watch = Stopwatch.StartNew();
        await new WaitProvider(TimeProvider.System).RunAsync(("duration", "120ms"));
        Assert.True(watch.ElapsedMilliseconds >= 100, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WaitEndsOnCancel()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var context = Steps.Context("wait", ("duration", "1h"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WaitProvider(TimeProvider.System).ExecuteAsync(context, cancel.Token));
    }

    [Fact]
    public async Task NotifyHandsTextToTheNotifier()
    {
        var notifier = new RecordingNotifier();
        await new NotifyProvider(notifier).RunAsync(("text", "Headphones ready"));
        Assert.Equal([("Test flow", "Headphones ready", true)], notifier.Shown);
    }

    [Fact]
    public async Task NotifyCanBeSilent()
    {
        var notifier = new RecordingNotifier();
        await new NotifyProvider(notifier).RunAsync(("text", "Quiet"), ("sound", "false"));
        Assert.Equal([("Test flow", "Quiet", false)], notifier.Shown);
    }

    [Fact]
    public async Task UnsetFlagIsOff()
    {
        var check = new FlagCheckProvider(new FlagStore());
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => check.RunAsync(("name", "gaming")));
        Assert.Equal("Flag \"gaming\" is off.", failure.Message);
        await check.RunAsync(("name", "gaming"), ("value", "false"));
    }

    [Fact]
    public async Task SetDefaultsToOnAndNamesIgnoreCase()
    {
        var flags = new FlagStore();
        await new FlagSetProvider(flags).RunAsync(("name", "Gaming"));
        await new FlagCheckProvider(flags).RunAsync(("name", "gaming"));
        await new FlagSetProvider(flags).RunAsync(("name", "GAMING"), ("value", "false"));
        await Assert.ThrowsAsync<StepFailedException>(() => new FlagCheckProvider(flags).RunAsync(("name", "gaming")));
    }

    [Fact]
    public void BuiltInsAreUniqueAndInTheCatalog()
    {
        var providers = BuiltInProviders.Create(TimeProvider.System, new RecordingNotifier());
        Assert.Equal(providers.Count, providers.Select(p => p.Id).Distinct().Count());
        Assert.All(providers, p => Assert.NotNull(ProviderCatalog.Find(p.Id)));
    }
}
