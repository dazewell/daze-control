using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>T25.3: a number setting holds a written number or one whole value; the range is checked when the step runs.</summary>
public class NumberValueTests
{
    private static readonly ValueList<ProviderDescriptor> Catalog = new(ProviderCatalog.All.Select(d => d.Id switch
    {
        "bluetooth.connect" => d with { Values = [new("device", ValueKind.Device, "Device") { Library = LibraryKind.Bluetooth }] },
        "power.battery" => d with { Values = [new("percent", ValueKind.Number, "Battery (%)"), new("label", ValueKind.Text, "Label")] },
        _ => d,
    }));

    private const string Header = """
        library:
          bluetooth:
            sony: { address: "B8:20:8E:ED:48:FC", name: Sony WH }
        flows:
          f:
            name: F
            inputs:
              note: { kind: text, name: Note, default: "5" }
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: sony } }
              - { id: batt, step: power.battery }

        """;

    private static string Volume(string percent) =>
        Header + $"      - {{ id: vol, step: audio.setVolume, after: [bt, batt], with: {{ percent: \"{percent}\" }} }}\n";

    private static Configuration Load(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private static List<Problem> Problems(string yaml) =>
        [.. ConfigValidator.Validate(Load(yaml), catalog: Catalog).Where(p => p.Path.Contains("vol", StringComparison.Ordinal))];

    private static ParamSpec Percent => Catalog.First(d => d.Id == "audio.setVolume").Params.First(p => p.Name == "percent");

    [Fact]
    public void ANumberValueWithAFallbackInRangeIsClean() => Assert.Empty(Problems(Volume("{batt.percent|50}")));

    [Fact]
    public void AWrittenNumberStillGetsItsRangeChecked()
    {
        Assert.Empty(Problems(Volume("40")));
        Assert.Equal("Use 0 to 100.", Assert.Single(Problems(Volume("140"))).Message);
        Assert.Equal("\"abc\" isn't a whole number.", Assert.Single(Problems(Volume("abc"))).Message);
    }

    [Fact]
    public void AValueWithTextAroundItIsOneError()
    {
        var p = Assert.Single(Problems(Volume("5{batt.percent|50}")));
        Assert.Equal(Severity.Error, p.Severity);
        Assert.Contains("one number or one value", p.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceValueIsAnError()
    {
        var p = Assert.Single(Problems(Volume("{bt.device}")));
        Assert.Equal((Severity.Error, "{bt.device} is a device, not a number."), (p.Severity, p.Message));
    }

    [Theory]
    [InlineData("{batt.label|50}")]
    [InlineData("{$note|50}")]
    [InlineData("{@time|50}")]
    public void ATextValueWarnsThatItMayFailWhenTheStepRuns(string text)
    {
        var p = Assert.Single(Problems(Volume(text)));
        Assert.Equal(Severity.Warning, p.Severity);
        Assert.Contains("fails when it runs", p.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{batt.percent|abc}", "isn't a whole number")]
    [InlineData("{batt.percent|42.5}", "isn't a whole number")]
    [InlineData("{batt.percent|101}", "Use 0 to 100.")]
    public void AWrittenFallbackIsRangeChecked(string text, string expected)
    {
        var p = Assert.Single(Problems(Volume(text)));
        Assert.Equal(Severity.Error, p.Severity);
        Assert.Contains(expected, p.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntegralWrittenFallbackIsAcceptedLikeTheStepReadsIt() => Assert.Empty(Problems(Volume("{batt.percent|42.0}")));

    [Fact]
    public void AnUnknownValueNameIsAnError()
    {
        var p = Assert.Single(Problems(Volume("{batt.nope}")));
        Assert.Equal(Severity.Error, p.Severity);
    }

    [Fact]
    public void ASpaceAroundTheReferenceIsTrimmedBeforeItIsClassified()
    {
        Assert.NotNull(StepValues.WholeRef("  {batt.percent|5}  "));
        Assert.Null(StepValues.WholeRef("{batt.percent} {batt.percent}"));
        Assert.Null(StepValues.WholeRef("{{batt.percent}"));
        Assert.Null(StepValues.WholeRef("50"));
        Assert.Empty(Problems(Volume(" {batt.percent|50} ")));
    }

    [Fact]
    public void TheRunnerFillsOnlyAWholeReference()
    {
        var config = Load(Volume("{batt.percent|50}"));
        var flow = config.Flows[0];
        var vol = (StepNode)flow.FindNode("vol")!;
        var values = new RunValues();
        var descriptor = Catalog.First(d => d.Id == "audio.setVolume");

        Assert.Equal("50", ((ScalarParam)StepValues.Fill(flow, vol, values, config.Library, out _, descriptor).With["percent"]).Text);
        values.SetNumber("batt", "percent", 35L);
        Assert.Equal("35", ((ScalarParam)StepValues.Fill(flow, vol, values, config.Library, out _, descriptor).With["percent"]).Text);

        var mixed = vol with { With = new([KeyValuePair.Create("percent", (ParamValue)"5{batt.percent}")]) };
        Assert.Same(mixed, StepValues.Fill(flow, mixed, values, config.Library, out _, descriptor));
        var plain = vol with { With = new([KeyValuePair.Create("percent", (ParamValue)"40")]) };
        Assert.Same(plain, StepValues.Fill(flow, plain, values, config.Library, out _, descriptor));
    }

    [Fact]
    public void AMissingValueFillsAsNothingSoTheStepCanSayItHasNoNumber()
    {
        var config = Load(Volume("{batt.percent}"));
        var vol = (StepNode)config.Flows[0].FindNode("vol")!;
        var filled = StepValues.Fill(config.Flows[0], vol, new RunValues(), config.Library, out _, Catalog.First(d => d.Id == "audio.setVolume"));
        Assert.Equal("", ((ScalarParam)filled.With["percent"]).Text);
    }

    [Fact]
    public void ANumberValueIsKeptForTheStepThatUsesItAndFollowsRenamesAndDeletes()
    {
        var config = Load(Volume("{batt.percent|50}"));
        var flow = config.Flows[0];

        Assert.Contains(("batt", "percent"), StepValues.Wanted(flow, Catalog));
        var renamed = StepValues.RewriteRefs(flow, (_, r) => r.Name == "batt" ? "{power.percent|50}" : null, Catalog);
        Assert.Equal("{power.percent|50}", ((ScalarParam)((StepNode)renamed.FindNode("vol")!).With["percent"]).Text);
        var dropped = StepValues.DropRefsTo(flow, new HashSet<string> { "batt" }, Catalog);
        Assert.Equal("50", ((ScalarParam)((StepNode)dropped.FindNode("vol")!).With["percent"]).Text);
    }

    [Fact]
    public void AFlowWithAValueInANumberSettingRoundTripsAsWritten()
    {
        var yaml = Volume("{batt.percent|50}");
        var config = Load(yaml);
        Assert.Equal(config, Load(ConfigYaml.Save(config)));
        Assert.Equal("{batt.percent|50}", ((ScalarParam)((StepNode)config.Flows[0].FindNode("vol")!).With["percent"]).Text);
        Assert.True(StepValues.Takes(Percent));
    }
}
