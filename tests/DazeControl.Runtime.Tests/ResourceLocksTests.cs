using DazeControl.Core.Catalog;
using DazeControl.Core.Model;

namespace DazeControl.Runtime.Tests;

public class ResourceLocksTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameNameWaitsForTheHolder()
    {
        var locks = new ResourceLocks();
        var first = await locks.AcquireAsync(["audio"], Token);

        var second = locks.AcquireAsync(["audio"], Token);
        Assert.False(second.IsCompleted);
        first.Dispose();

        (await second).Dispose();
    }

    [Fact]
    public async Task DifferentNamesDontWait()
    {
        var locks = new ResourceLocks();
        using var a = await locks.AcquireAsync(["bluetooth:technics"], Token);

        var b = locks.AcquireAsync(["bluetooth:sony"], Token);

        Assert.True(b.IsCompletedSuccessfully);
        (await b).Dispose();
    }

    [Fact]
    public async Task CancelledWaitHoldsNothing()
    {
        var locks = new ResourceLocks();
        var holder = await locks.AcquireAsync(["b"], Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        // "a" is free and taken first (ordinal order), then the wait on "b" is cancelled: "a" must be let go.
        var waiting = locks.AcquireAsync(["b", "a"], cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        holder.Dispose();

        var again = locks.AcquireAsync(["a"], Token);
        Assert.True(again.IsCompletedSuccessfully);
        (await again).Dispose();
    }

    [Fact]
    public async Task DisposingTwiceReleasesOnce()
    {
        var locks = new ResourceLocks();
        var first = await locks.AcquireAsync(["x"], Token);
        first.Dispose();
        first.Dispose();

        using var second = await locks.AcquireAsync(["x"], Token);
        Assert.False(locks.AcquireAsync(["x"], Token).IsCompleted);
    }

    [Fact]
    public void ResourceNamesTakeParamValues()
    {
        var descriptor = ProviderCatalog.Find("bluetooth.connect")!;
        var node = new StepNode("bt", "bluetooth.connect") { With = new([KeyValuePair.Create("device", (ParamValue)"technics")]) };

        Assert.Equal(["bluetooth:technics"], FlowRunner.ResourcesOf(descriptor, node));
    }
}
