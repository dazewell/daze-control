using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;
using static DazeControl.Runtime.Tests.PromptRunTests;

namespace DazeControl.Runtime.Tests;

/// <summary>T24.8 (D-140): a Check address that answered but failed still hands its timing and status to the failure link.</summary>
public sealed class NetworkReachRunTests : IDisposable
{
    private const string Yaml = """
        flows:
          f:
            name: Check the page
            nodes:
              - { id: r, step: network.reach, with: { address: "https://example.com", status: "200-299" } }
              - { id: n, step: notify, with: { text: "got {r.status} in {r.ms} ms: {r.result}" }, after: [{ id: r, when: failure }] }
        """;

    private readonly ManualTime _time = new();

    public void Dispose() => _time.Dispose();

    [Fact]
    public async Task TheFailureLinkReadsTheStatusAndTimeOfAnAnswerThatDidNotCount()
    {
        var config = ConfigYaml.Load(Yaml).Config!;
        List<string> said = [];
        var runner = new FlowRunner(
        [
            new Recorder("network.reach", c =>
            {
                c.SetNumber("status", 503);
                c.SetNumber("ms", 42);
                c.SetValue("result", "example.com answered 503, which isn't one that counts");
                throw new StepFailedException("example.com answered 503, which isn't one that counts");
            }),
            new Recorder("notify", c => said.Add(((ScalarParam)c.Node.With["text"]).Text)),
        ], _time);

        await Bound(runner.RunAsync(config.Flows.Single(), config.Library, TestContext.Current.CancellationToken));

        Assert.Equal(["got 503 in 42 ms: example.com answered 503, which isn't one that counts"], said);
    }
}
