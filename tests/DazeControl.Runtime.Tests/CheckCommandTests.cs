using DazeControl.Core.Model;
using DazeControl.Core.Yaml;
using DazeControl.Ipc;

namespace DazeControl.Runtime.Tests;

/// <summary><c>dctl check</c> (D-117): one check step as a test-step request, with the user's Library.</summary>
public class CheckCommandTests
{
    [Fact]
    public void BuildsATestStepRequestWithTheSettingsAndTheLibrary()
    {
        const string config = """
            library:
              audio:
                tv: { match: Samsung }
            flows:
              other:
                nodes:
                  - { id: a, step: wait, with: { duration: 1s } }

            """;
        var (request, error) = CheckCommand.Request(["network.wifi", "network=Home Wi-Fi", "is=notConnected"], config);
        Assert.Null(error);
        Assert.Equal(Ops.TestStep, request!.Op);
        var sent = ConfigYaml.Load(request.Config!).Config!;
        var flow = Assert.Single(sent.Flows); // the user's own flows stay out
        Assert.Equal(CheckCommand.Flow, flow.Id);
        var step = (StepNode)flow.FindNode(request.Node!)!;
        Assert.Equal("network.wifi", step.Step);
        Assert.Equal("Home Wi-Fi", ((ScalarParam)step.With["network"]).Text);
        Assert.Equal("notConnected", ((ScalarParam)step.With["is"]).Text);
        Assert.Single(sent.Library.Audio);
    }

    [Theory]
    [InlineData("wait")] // not a check
    [InlineData("network.wifi", "ssid=Home")] // no such setting
    [InlineData("network.wifi", "Home")] // not name=value
    public void RefusesWhatIsntACheckOrItsSettings(params string[] words)
    {
        var (request, error) = CheckCommand.Request(words, null);
        Assert.Null(request);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParsesUpToSixteenWords()
    {
        Assert.Equal(["power.battery", "percent=30"], CommandLine.Parse("check", ["power.battery", "percent=30"]).Words);
        Assert.Throws<CommandLineException>(() => CommandLine.Parse("check", []));
    }
}
