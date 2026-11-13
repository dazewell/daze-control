using System.Text.Json;
using DazeControl.Core.Runs;
using DazeControl.Ipc;

namespace DazeControl.Runtime.Tests;

/// <summary>The shape of <c>dctl --json</c> is a contract for scripts (T27.8, D-142): these are golden strings; change one only by adding a field.</summary>
public class ScriptJsonTests
{
    [Fact]
    public void ListGolden()
    {
        FlowInfo[] flows =
        [
            new() { Id = "tv", Name = "TV only", Enabled = true, Runnable = true, Hotkeys = ["Alt+T"] },
            new() { Id = "bad", Name = "Bad", Enabled = true, Runnable = false, Errors = 2 },
            new() { Id = "old", Name = "Old", Enabled = true, Runnable = true, Stale = true, Running = true },
            new() { Id = "off", Name = "Off", Enabled = false },
        ];

        Assert.Equal(
            """{"engineRunning":true,"flows":[""" +
            """{"id":"tv","name":"TV only","state":"ready","enabled":true,"runnable":true,"stale":false,"running":false,"hotkeys":["Alt+T"],"errors":0},""" +
            """{"id":"bad","name":"Bad","state":"needsFixing","enabled":true,"runnable":false,"stale":false,"running":false,"hotkeys":[],"errors":2},""" +
            """{"id":"old","name":"Old","state":"lastGoodVersion","enabled":true,"runnable":true,"stale":true,"running":true,"hotkeys":[],"errors":0},""" +
            """{"id":"off","name":"Off","state":"off","enabled":false,"runnable":false,"stale":false,"running":false,"hotkeys":[],"errors":0}]}""",
            ScriptJson.Flows(flows, engineRunning: true));
        Assert.Equal("""{"engineRunning":false,"flows":[]}""", ScriptJson.Flows([], engineRunning: false));
    }

    [Fact]
    public void StatusGolden() =>
        Assert.Equal(
            """{"version":"1.2.3","processId":42,"configPath":"C:\\x\\c.yaml","errors":1,"warnings":0,"running":["TV only"],"shortcutsPaused":true,"shortcutProblems":["Alt+T: taken"]}""",
            ScriptJson.Status(new EngineStatus
            {
                Version = "1.2.3", ProcessId = 42, ConfigPath = @"C:\x\c.yaml", Errors = 1, ShortcutsPaused = true,
                Running = ["TV only"], ShortcutProblems = ["Alt+T: taken"],
            }));

    [Fact]
    public void HistoryGoldenHoldsASummaryOnly()
    {
        var run = new RunRecord(Guid.Parse("11111111-2222-3333-4444-555555555555"), "tv", "TV only", "Command line",
            new DateTimeOffset(2026, 11, 13, 12, 30, 5, TimeSpan.FromHours(2)), TimeSpan.FromMilliseconds(1234.5678), FlowOutcome.PartiallySucceeded,
            [new NodeResult("n1", NodeOutcome.Failed, 1, "secret-looking error") { Summary = "token=abc" }]);

        var json = ScriptJson.History([run, run with { Outcome = FlowOutcome.Cancelled }]);

        Assert.Equal(
            """{"runs":[""" +
            """{"runId":"11111111-2222-3333-4444-555555555555","flowId":"tv","flowName":"TV only","trigger":"Command line","started":"2026-11-13T10:30:05.000Z","durationSeconds":1.235,"outcome":"partiallySucceeded"},""" +
            """{"runId":"11111111-2222-3333-4444-555555555555","flowId":"tv","flowName":"TV only","trigger":"Command line","started":"2026-11-13T10:30:05.000Z","durationSeconds":1.235,"outcome":"cancelled"}]}""",
            json);
        Assert.DoesNotContain("token", json);
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void DevicesGolden() =>
        Assert.Equal(
            """{"kind":"bluetooth","devices":[""" +
            """{"name":"D9:E0:8E:C2:96:85","detail":"MX Master 3S","state":"connected","notConnectable":false},""" +
            """{"name":"Müller \"TV\"","detail":null,"state":null,"notConnectable":true}]}""",
            ScriptJson.Devices("bluetooth",
            [
                new DeviceInfo { Name = "D9:E0:8E:C2:96:85", Detail = "MX Master 3S", State = "connected", Id = "hidden", Value = "hidden" },
                new DeviceInfo { Name = "Müller \"TV\"", NotConnectable = true },
            ]));

    [Fact]
    public void CheckFlagAndPauseGolden()
    {
        Assert.Equal("""{"check":"network.wifi","ok":true,"message":"On Home"}""", ScriptJson.Check("network.wifi", true, "On Home"));
        Assert.Equal("""{"check":"process.check","ok":false,"message":"Spotify isn't running"}""", ScriptJson.Check("process.check", false, "Spotify isn't running"));
        Assert.Equal("""{"flag":"gaming","on":true}""", ScriptJson.Flag("gaming", true));
        Assert.Equal("""{"paused":false}""", ScriptJson.Pause(false));
    }

    [Fact]
    public void EveryDocumentIsOneLineOfValidJson()
    {
        string[] documents =
        [
            ScriptJson.Flows([new FlowInfo { Id = "a", Name = "line\nbreak \"quoted\"" }], true),
            ScriptJson.Devices(null, [new DeviceInfo { Name = "a\r\nb" }]),
            ScriptJson.Check("x", true, "two\nlines"),
        ];

        foreach (var document in documents)
        {
            Assert.DoesNotContain('\n', document);
            Assert.DoesNotContain('\r', document);
            using var parsed = JsonDocument.Parse(document);
            Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        }
    }
}
