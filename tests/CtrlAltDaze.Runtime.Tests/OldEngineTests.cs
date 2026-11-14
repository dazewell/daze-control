using CtrlAltDaze.Runtime.Lifecycle;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>T29.2 (D-152): the old engine is asked to exit and waited for through its mutex.</summary>
public sealed class OldEngineTests
{
    private readonly string _name = @"Local\CtrlAltDaze.Tests.OldEngine-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void NoOldEngineDoesNothing()
    {
        var asked = false;

        Assert.Null(OldEngine.Stop(_name, () => asked = true, TimeSpan.FromSeconds(1)));
        Assert.False(asked);
    }

    [Fact]
    public void AnOldEngineThatExitsIsWaitedFor()
    {
        using var held = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        var engine = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, _name);
            held.Set();
            exit.Wait();
            mutex.ReleaseMutex();
        });
        engine.Start();
        held.Wait(TestContext.Current.CancellationToken);

        Assert.Equal("setup: the old engine exited", OldEngine.Stop(_name, exit.Set, TimeSpan.FromSeconds(10)));
        engine.Join();
    }

    [Fact]
    public void AnOldEngineThatStaysIsReportedAfterTheWait()
    {
        using var held = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var engine = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, _name);
            held.Set();
            done.Wait();
            mutex.ReleaseMutex();
        });
        engine.Start();
        held.Wait(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal("setup: the old engine didn't exit within 0 s",
                OldEngine.Stop(_name, () => { }, TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            done.Set();
            engine.Join();
        }
    }
}
