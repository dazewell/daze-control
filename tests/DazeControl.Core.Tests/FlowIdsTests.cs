using DazeControl.Core.Model;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>Flow ids from names (D-075) and the steps that name a flow (D-075, D-076).</summary>
public class FlowIdsTests
{
    [Theory]
    [InlineData("Morning", "morning")]
    [InlineData("Connect Technics", "connect-technics")]
    [InlineData("  TV  &  sound!! ", "tv-sound")]
    [InlineData("Wi-Fi_off (work)", "wi-fi-off-work")]
    [InlineData("Café crème", "cafe-creme")]
    [InlineData("Ñandú Über", "nandu-uber")]
    [InlineData("Наушники Sony", "наушники-sony")]
    [InlineData("Звук 2", "звук-2")]
    [InlineData("🎧 Buds", "buds")]
    [InlineData("🎧", "flow")]
    [InlineData("!!!", "flow")]
    [InlineData("", "flow")]
    [InlineData(null, "flow")]
    [InlineData("Ünïcödé", "unicode")]
    public void SlugMakesASafeId(string? name, string expected) => Assert.Equal(expected, FlowIds.Slug(name));

    [Fact]
    public void ALongNameIsCutAtADash()
    {
        var slug = FlowIds.Slug("Connect the living room speakers and the projector screen");

        Assert.True(slug.Length <= FlowIds.MaxLength, slug);
        Assert.Equal("connect-the-living-room-speakers-and-the", slug);
        Assert.False(slug.EndsWith('-'));
    }

    [Fact]
    public void ALongWordIsCutAtTheLimit()
    {
        var slug = FlowIds.Slug(new string('a', 60));

        Assert.Equal(new string('a', 40), slug);
    }

    [Fact]
    public void UniqueAddsANumberOnAClash()
    {
        Assert.Equal("morning", FlowIds.Unique("morning", new HashSet<string>()));
        Assert.Equal("morning-2", FlowIds.Unique("morning", new HashSet<string> { "morning" }));
        Assert.Equal("morning-3", FlowIds.Unique("morning", new HashSet<string> { "morning", "morning-2" }));
    }

    private static readonly Configuration Config = ConfigYaml.Load("""
        flows:
          tv:
            name: TV
            nodes:
              - { id: note, step: notify, with: { text: tv } }
          evening:
            name: Evening
            nodes:
              - { id: call, step: flow.run, with: { flow: tv } }
              - { id: other, step: flow.run, with: { flow: night, wait: "false" } }
          night:
            name: Night
            nodes:
              - { id: again, step: flow.run, with: { flow: tv } }
        """).Config!;

    private static string? Called(Configuration config, string flowId, string nodeId) =>
        ((StepNode)config.Flows.Single(f => f.Id == flowId).FindNode(nodeId)!).With["flow"].ToString();

    [Fact]
    public void CallersAreTheOtherFlowsThatRunIt()
    {
        Assert.Equal(["evening", "night"], FlowRefs.Callers(Config, "tv").Select(f => f.Id));
        Assert.Equal(["evening"], FlowRefs.Callers(Config, "night").Select(f => f.Id));
        Assert.Empty(FlowRefs.Callers(Config, "evening"));
    }

    [Fact]
    public void ANotifyTextThatLooksLikeAnIdIsNotACall()
    {
        var renamed = FlowRefs.Rename(Config, "tv", "television");

        Assert.Equal("tv", ((StepNode)renamed.Flows[0].FindNode("note")!).With["text"].ToString());
    }

    [Fact]
    public void RenameRewritesEveryCallerAndNothingElse()
    {
        var renamed = FlowRefs.Rename(Config, "tv", "television");

        Assert.Equal("television", Called(renamed, "evening", "call"));
        Assert.Equal("television", Called(renamed, "night", "again"));
        Assert.Equal("night", Called(renamed, "evening", "other"));
        Assert.Equal("false", ((StepNode)renamed.Flows[1].FindNode("other")!).With["wait"].ToString());
        Assert.Equal(["tv", "evening", "night"], renamed.Flows.Select(f => f.Id)); // the flow itself is the caller's job
    }

    [Fact]
    public void RenameWithNoCallersOrTheSameIdChangesNothing()
    {
        Assert.Same(Config, FlowRefs.Rename(Config, "evening", "late"));
        Assert.Same(Config, FlowRefs.Rename(Config, "tv", "tv"));
    }
}
