using System.Text.Json;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;

namespace DazeControl.Runtime.Tests;

/// <summary>"Only if" at run time (D-081, DESIGN B4), with a fake probe and the fake provider.</summary>
public sealed class ConditionRunTests : IDisposable
{
    private sealed class FakeProbe(string fact) : IFactProbe
    {
        public string Fact => fact;

        public bool Value { get; set; } = true;

        public Exception? Throws { get; set; }

        public bool Hang { get; set; }

        public int Reads { get; private set; }

        public async Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
        {
            Reads++;
            if (Hang)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return Throws is null ? new(Value, Value ? "Technics is connected" : "Technics isn't connected") : throw Throws;
        }
    }

    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly FakeProbe _probe = new("test.connected");

    // "Only if it isn't connected": runs when the fact is false.
    private static readonly List<ProviderDescriptor> Catalog =
    [
        .. ProviderCatalog.All.Select(d => d.Id == "notify"
            ? d with { Conditions = [new ConditionSpec("disconnected", "It isn't connected", "test.connected", false)] }
            : d),
    ];

    public void Dispose() => _time.Dispose();

    private FlowRunner Runner(bool probes = true) =>
        new([_provider], _time, catalog: Catalog, probes: probes ? [_probe] : null);

    private static StepNode Step(string id, string? only = "disconnected", params Edge[] after) =>
        new(id, "notify") { Only = only, After = new(after) };

    private Task<RunResult> Run(params Node[] nodes) =>
        FlowRunnerTests.Bound(Runner().RunAsync(new Flow("f", "F", [.. nodes]), Library.Empty, TestContext.Current.CancellationToken));

    [Fact]
    public async Task UnmetSkipsWithANoteAndSuccessDependentsRunWhileFailureEdgesDont()
    {
        var run = await Run(Step("a"), Step("ok", null, new Edge("a")), Step("fallback", null, new Edge("a", EdgeWhen.Failure)));

        Assert.Equal(["ok"], _provider.Attempts.Select(a => a.Step));
        var a = run.Nodes.First(n => n.NodeId == "a");
        Assert.Equal(NodeOutcome.Succeeded, a.Outcome);
        Assert.Equal("Technics is connected", a.SkippedBecause);
        Assert.Equal(1, a.Attempts);
        Assert.Equal(NodeOutcome.NotNeeded, run.Nodes.First(n => n.NodeId == "fallback").Outcome);
        Assert.Equal(FlowOutcome.Succeeded, run.Outcome);
    }

    [Fact]
    public async Task ATypeDefaultSkipsAndAStepWithAlwaysStillRuns()
    {
        var defaults = new ValueDictionary<StepDefault>(new[] { KeyValuePair.Create("notify", new StepDefault { Only = "disconnected" }) });
        var flow = StepDefaults.Apply(new Flow("f", "F", [Step("inherit", null), Step("always", "always")]), defaults, Catalog);

        var run = await FlowRunnerTests.Bound(Runner().RunAsync(flow, Library.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(["always"], _provider.Attempts.Select(a => a.Step));
        Assert.Equal("Technics is connected", run.Nodes.First(n => n.NodeId == "inherit").SkippedBecause);
        Assert.Equal(1, _probe.Reads);
    }

    [Fact]
    public async Task MetRunsTheStepWithNoNote()
    {
        _probe.Value = false;

        var run = await Run(Step("a"));

        Assert.Equal(["a"], _provider.Attempts.Select(a => a.Step));
        Assert.Null(run.Nodes[0].SkippedBecause);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("always")]
    public async Task AlwaysAndNoOnlyNeverAskTheProbe(string? only)
    {
        var run = await Run(Step("a", only));

        Assert.Equal(0, _probe.Reads);
        Assert.Equal(["a"], _provider.Attempts.Select(a => a.Step));
        Assert.Null(run.Nodes[0].SkippedBecause);
    }

    [Fact]
    public async Task AProbeThatThrowsLetsTheStepRun()
    {
        _probe.Throws = new InvalidOperationException("radio off");

        var run = await Run(Step("a"));

        Assert.Equal(["a"], _provider.Attempts.Select(a => a.Step));
        Assert.Equal(FlowOutcome.Succeeded, run.Outcome);
        Assert.Null(run.Nodes[0].SkippedBecause);
    }

    [Fact]
    public async Task AProbeThatHangsEndsWithTheStepsTimeoutAndNeverSkips()
    {
        _probe.Hang = true;
        var run = Run(Step("a") with { Timeout = TimeSpan.FromSeconds(2) });

        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(2));
        var result = await run;

        Assert.Empty(_provider.Attempts);
        Assert.Equal(NodeOutcome.Failed, result.Nodes[0].Outcome);
        Assert.Null(result.Nodes[0].SkippedBecause);
    }

    [Fact]
    public async Task ARetryDoesNotAskAgain()
    {
        _probe.Value = false;
        var calls = 0;
        _provider.On("a", (_, _) => ++calls == 1 ? FakeProvider.Fail("nope") : Task.CompletedTask);

        var run = await Run(Step("a") with { RetryCount = 1 });

        Assert.Equal(2, run.Nodes[0].Attempts);
        Assert.Equal(1, _probe.Reads);
    }

    [Fact]
    public async Task ACardMemberSkippedByConditionCountsAsDone()
    {
        var run = await Run(Step("a"), Step("b", null), new GroupNode("g", ["a", "b"]), Step("after", null, new Edge("g")));

        Assert.Equal(["b", "after"], _provider.Attempts.Select(a => a.Step).Order().Reverse());
        Assert.Equal(FlowOutcome.Succeeded, run.Outcome);
    }

    [Fact]
    public async Task ARunnerWithoutProbesAssumesTheConditionIsMet()
    {
        var run = await FlowRunnerTests.Bound(Runner(probes: false).RunAsync(
            new Flow("f", "F", [Step("a")]), Library.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(["a"], _provider.Attempts.Select(a => a.Step));
        Assert.Null(run.Nodes[0].SkippedBecause);
    }

    [Fact]
    public async Task TheDryRunnerRunsConditionalStepsNormally()
    {
        var dry = EngineController.CreateDryRunner(_time);

        var run = await FlowRunnerTests.Bound(dry.RunAsync(
            new Flow("f", "F", [new StepNode("a", "notify") { Only = "disconnected" }]), Library.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, run.Outcome);
        Assert.Null(run.Nodes[0].SkippedBecause);
    }

    [Fact]
    public void ANoteSurvivesTheHistoryFileAndOldRecordsStillRead()
    {
        var result = new NodeResult("a", NodeOutcome.Succeeded) { SkippedBecause = "Technics is connected" };
        var json = JsonSerializer.Serialize(new RunRecord(Guid.NewGuid(), "f", "F", "t", DateTimeOffset.UnixEpoch, TimeSpan.Zero,
            FlowOutcome.Succeeded, [result]), HistoryJson.Default.RunRecord);
        Assert.Equal("Technics is connected", JsonSerializer.Deserialize(json, HistoryJson.Default.RunRecord)!.Nodes[0].SkippedBecause);

        var old = json.Replace("\"skippedBecause\":\"Technics is connected\",", "", StringComparison.Ordinal)
            .Replace(",\"skippedBecause\":\"Technics is connected\"", "", StringComparison.Ordinal);
        Assert.DoesNotContain("skippedBecause", old, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize(old, HistoryJson.Default.RunRecord)!.Nodes[0].SkippedBecause);
    }
}
