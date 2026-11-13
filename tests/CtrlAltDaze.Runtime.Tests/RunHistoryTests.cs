using System.Text.Json;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using static CtrlAltDaze.Runtime.Tests.SchedulerTests;

namespace CtrlAltDaze.Runtime.Tests;

public sealed class RunHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-history-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    private string HistoryFile => Path.Combine(_dir, "history.jsonl");

    public void Dispose()
    {
        _time.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static RunRecord Record(int n) => new(Guid.NewGuid(), "f" + n, "Flow " + n, "Alt+Z",
        new DateTimeOffset(2026, 11, 1, 0, 0, n % 60, TimeSpan.Zero), TimeSpan.FromSeconds(n), FlowOutcome.PartiallySucceeded,
        [new NodeResult("a", NodeOutcome.FailedIgnored, 2, "Didn't connect.", TimeSpan.FromMilliseconds(1500))]);

    private static void AssertSame(RunRecord expected, RunRecord actual)
    {
        Assert.Equal(expected with { Nodes = [] }, actual with { Nodes = [] });
        Assert.Equal(expected.Nodes, actual.Nodes);
    }

    [Fact]
    public void PersistsAndReloadsRecords()
    {
        var record = Record(1);
        new RunHistory(HistoryFile).Add(record);

        var reloaded = new RunHistory(HistoryFile);

        AssertSame(record, Assert.Single(reloaded.Recent));
        Assert.Null(reloaded.LoadError);
    }

    [Fact]
    public void ShownValuesRoundTripAndOldLinesStillLoad()
    {
        var node = new NodeResult("w", NodeOutcome.Succeeded, Order: 1)
        {
            Values = [new ShownValue("Wi-Fi network", "Home"), new ShownValue("Output", "", Hidden: true)],
            Used = [new ShownValue("Message", "On Home")],
        };
        var record = Record(3) with { Nodes = [node] };
        var history = new RunHistory(HistoryFile);
        history.Add(record);
        // A line written before Values existed, and one from a newer engine with a field this one doesn't know.
        File.AppendAllText(HistoryFile,
            "{\"runId\":\"" + Guid.NewGuid() + "\",\"flowId\":\"old\",\"flowName\":\"Old\",\"trigger\":\"\",\"started\":\"2026-10-01T00:00:00+00:00\"," +
            "\"duration\":\"00:00:01\",\"outcome\":\"succeeded\",\"nodes\":[{\"nodeId\":\"a\",\"outcome\":\"succeeded\",\"attempts\":1,\"later\":5}]}\n");

        var reloaded = new RunHistory(HistoryFile).Recent;

        Assert.Equal(2, reloaded.Count);
        var back = reloaded[0].Nodes.Single();
        Assert.Equal(node.Values, back.Values); // ShownValue is a record: element-wise
        Assert.Equal(node.Used, back.Used);
        Assert.Null(reloaded[1].Nodes.Single().Values);
        Assert.Null(reloaded[1].Nodes.Single().Used);
    }

    [Fact]
    public void ShownValuesOfAHundredRunsStayNearAMegabyte()
    {
        var text = new string('x', ShownValues.MaxLength);
        var history = new RunHistory(HistoryFile);
        for (var i = 0; i < RunHistory.Capacity; i++)
            history.Add(Record(i) with
            {
                Nodes = [.. Enumerable.Range(0, 10).Select(n => new NodeResult("s" + n, NodeOutcome.Succeeded, Order: n + 1)
                    {
                        // The most a step can store: MaxChars of text over MaxLines lines.
                        Values = [new ShownValue("Value", text), new ShownValue("Value", text), new ShownValue("Value", text)],
                        Used = [new ShownValue("Message", text), new ShownValue("Message", "", Hidden: true), new ShownValue("Message", "", Hidden: true)],
                    })],
            });

        Assert.InRange(new FileInfo(HistoryFile).Length, 1, 1_600_000); // at the step budget (6 lines, 1,024 characters): about 1.45 MB
    }

    [Fact]
    public void NotNeededRoundTrips()
    {
        var record = Record(2) with
        {
            Outcome = FlowOutcome.Succeeded,
            Nodes = [new NodeResult("bt", NodeOutcome.Succeeded, Order: 1), new NodeResult("connect", NodeOutcome.NotNeeded, 0)],
        };
        new RunHistory(HistoryFile).Add(record);

        AssertSame(record, Assert.Single(new RunHistory(HistoryFile).Recent));
        Assert.Contains("\"notNeeded\"", File.ReadAllText(HistoryFile), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsTheLastHundredAndTrimsTheFile()
    {
        var history = new RunHistory(HistoryFile);
        for (var i = 0; i < 250; i++)
            history.Add(Record(i));

        Assert.Equal(RunHistory.Capacity, history.Recent.Count);
        Assert.Equal("f249", history.Recent[^1].FlowId);
        Assert.True(File.ReadAllLines(HistoryFile).Length < 2 * RunHistory.Capacity);
        Assert.Equal(history.Recent.Select(r => r.RunId), new RunHistory(HistoryFile).Recent.Select(r => r.RunId));
    }

    [Fact]
    public void SkipsCorruptLines()
    {
        Directory.CreateDirectory(_dir);
        var good = Record(7);
        new RunHistory(HistoryFile).Add(good);
        File.AppendAllText(HistoryFile, "{ not json\n");

        AssertSame(good, Assert.Single(new RunHistory(HistoryFile).Recent));
    }

    [Fact]
    public void CorruptLinesDontCostHealthyRecords()
    {
        Directory.CreateDirectory(_dir);
        var records = Enumerable.Range(0, RunHistory.Capacity + 1).Select(Record).ToList();
        File.WriteAllLines(HistoryFile, records.Select(r => JsonSerializer.Serialize(r, HistoryJson.Default.RunRecord)));
        File.AppendAllText(HistoryFile, "{ not json\n");

        var reloaded = new RunHistory(HistoryFile).Recent;

        Assert.Equal(records.Skip(1).Select(r => r.RunId), reloaded.Select(r => r.RunId));
    }

    [Fact]
    public void RecordAfterACutOffLineSurvives()
    {
        Directory.CreateDirectory(_dir);
        var first = Record(1);
        new RunHistory(HistoryFile).Add(first);
        File.AppendAllText(HistoryFile, "{\"runId\":\"0"); // a crash mid-write, no newline
        var second = Record(2);

        new RunHistory(HistoryFile).Add(second);

        Assert.Equal([first.RunId, second.RunId], new RunHistory(HistoryFile).Recent.Select(r => r.RunId));
    }

    [Fact]
    public void UnwritablePathKeepsMemoryHistoryAndReports()
    {
        Directory.CreateDirectory(_dir);
        var history = new RunHistory(_dir); // a directory, not a file
        Exception? reported = null;
        history.WriteFailed += e => reported = e;

        history.Add(Record(1));

        Assert.Single(history.Recent);
        Assert.NotNull(reported);
    }

    [Fact]
    public async Task RunnerRaisesEventsForEveryNode()
    {
        var events = new List<RunEvent>();
        var provider = new FakeProvider().On("a", (_, _) => FakeProvider.Fail("no"));
        var runner = new FlowRunner([provider], _time, events: e =>
        {
            lock (events)
                events.Add(e);
        });

        var result = await runner.RunAsync(Flow(Step("a", "onFailure: skip"), Step("b", "after: [a]")), Library.Empty, TestContext.Current.CancellationToken);

        Assert.IsType<RunStarted>(events[0]);
        Assert.Equal(result.RunId, events[0].RunId);
        Assert.Equal(["a"], events.OfType<NodeStarted>().Select(e => e.NodeId));
        Assert.Equal([("a", NodeOutcome.Failed), ("b", NodeOutcome.Skipped)],
            events.OfType<NodeFinished>().Select(e => (e.Result.NodeId, e.Result.Outcome)));
        Assert.Equal(FlowOutcome.Failed, Assert.IsType<RunFinished>(events[^1]).Outcome);
    }

    [Fact]
    public async Task LauncherRecordsFinishedRunsWithTheirTrigger()
    {
        var history = new RunHistory(null);
        var launcher = new FlowLauncher(new FlowRunner([new FakeProvider()], _time), history);

        await launcher.TriggerAsync(Flow(Step("a")), Library.Empty, "Alt+Z");

        var record = Assert.Single(history.Recent);
        Assert.Equal(("f", "Alt+Z", FlowOutcome.Succeeded), (record.FlowId, record.Trigger, record.Outcome));
    }
}
