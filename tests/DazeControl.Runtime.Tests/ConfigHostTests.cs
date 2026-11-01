namespace DazeControl.Runtime.Tests;

internal sealed class FakeChanges : IFileChangeSource
{
    public event Action? Changed;

    public void Raise() => Changed?.Invoke();

    public void Dispose()
    {
    }
}

public sealed class ConfigHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-host-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeChanges _changes = new();

    private string ConfigFile => Path.Combine(_dir, "config.yaml");

    public ConfigHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _time.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void Write(string flows) => File.WriteAllText(ConfigFile, "version: 1\nflows:\n" + flows);

    private static string Flow(string id, string step = "notify, with: { text: x }") =>
        $"  {id}: {{ name: {id}, nodes: [{{ id: a, step: {step} }}] }}\n";

    private ConfigHost Host() => new(ConfigFile, _time, _changes);

    [Fact]
    public void MissingFileIsAnEmptyConfiguration()
    {
        using var host = Host();

        Assert.Empty(host.Current.Flows);
        Assert.Empty(host.Current.Problems);
    }

    [Fact]
    public void ValidFlowsAreRunnable()
    {
        Write(Flow("one") + Flow("two"));
        using var host = Host();

        Assert.Equal(["one", "two"], host.Current.RunnableFlows.Select(f => f.Id));
    }

    [Fact]
    public void BrokenEditKeepsTheLastValidVersionOfThatFlowOnly()
    {
        Write(Flow("one") + Flow("two"));
        using var host = Host();
        var before = host.Current.Flows[0].Running;

        Write(Flow("one", "nope") + Flow("two", "wait, with: { duration: 1s }"));
        host.Reload();

        var one = host.Current.Flows[0];
        Assert.True(one.IsStale);
        Assert.Same(before, one.Running);
        Assert.NotEmpty(one.Problems);
        Assert.Equal("wait", ((Core.Model.StepNode)host.Current.Flows[1].Running!.Nodes[0]).Step);
    }

    [Fact]
    public void NeverValidFlowIsDisabled()
    {
        Write(Flow("bad", "nope"));
        using var host = Host();

        Assert.Null(host.Current.Flows[0].Running);
        Assert.Empty(host.Current.RunnableFlows);
    }

    [Fact]
    public void UnreadableFileKeepsEverythingAndReportsIt()
    {
        Write(Flow("one"));
        using var host = Host();

        File.WriteAllText(ConfigFile, "flows: [");
        host.Reload();

        Assert.Equal(["one"], host.Current.RunnableFlows.Select(f => f.Id));
        Assert.Null(Assert.Single(host.Current.Problems).FlowId);
    }

    [Fact]
    public void DisabledFlowIsNotRunnable()
    {
        File.WriteAllText(ConfigFile, "flows: { off: { enabled: false, nodes: [{ id: a, step: notify, with: { text: x } }] } }");
        using var host = Host();

        Assert.NotNull(host.Current.Flows[0].Running);
        Assert.Empty(host.Current.RunnableFlows);
    }

    [Fact]
    public async Task ReloadsOnceAfterChangesSettle()
    {
        Write(Flow("one"));
        using var host = Host();
        var reloads = 0;
        host.Changed += _ => reloads++;
        await _time.WaitForTimerAsync(); // the debounce timer

        Write(Flow("one") + Flow("two"));
        _changes.Raise();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _changes.Raise(); // a second write in the burst restarts the wait
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, reloads);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, reloads);
        Assert.Equal(2, host.Current.Flows.Count);
    }
}
