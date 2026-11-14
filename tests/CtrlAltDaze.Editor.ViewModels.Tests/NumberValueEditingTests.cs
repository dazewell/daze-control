using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Editor.ViewModels.Inspector;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>T25.3: "Use a value" on a number setting: the pill replaces the number, one write each way, no devices offered.</summary>
public sealed class NumberValueEditingTests
{
    private static readonly Flow Flow = new("f", "F",
    [
        new StepNode("bt", "bluetooth.connect"),
        new StepNode("ps", "script.powershell") { After = [new Edge("bt")] },
        new StepNode("vol", "audio.setVolume") { After = [new Edge("ps")] },
    ]);

    private static ParamFieldViewModel Field(ParamValue? value, List<ParamValue?> writes, bool context = true)
    {
        var spec = ProviderCatalog.Find("audio.setVolume")!.Params.First(p => p.Name == "percent");
        var field = new ParamFieldViewModel(spec, (_, v) => writes.Add(v), _ => true);
        if (context)
            field.SetValueContext(Flow, "vol");
        field.Load(value, null);
        return field;
    }

    [Fact]
    public void AWrittenNumberKeepsTheBoxAndOffersUseAValue()
    {
        var field = Field(new ScalarParam("40"), []);

        Assert.True(field.ShowsNumberBox);
        Assert.False(field.UsesValue);
        Assert.True(field.ShowsValueButton);
        Assert.Equal(40, field.Number);
        Assert.Equal("Use a value for Volume (%)", field.UseValueName);
        Assert.False(field.HasPills);
    }

    [Fact]
    public void AValueTakesTheBoxsPlaceAndShowsAsAPill()
    {
        var field = Field(new ScalarParam("{ps.exitCode|50}"), []);

        Assert.True(field.UsesValue);
        Assert.False(field.ShowsNumberBox);
        Assert.True(field.HasPills);
        Assert.StartsWith("Exit code · ", Assert.Single(field.Pills, p => p.IsPill).Text, StringComparison.Ordinal);
        Assert.Equal(double.NaN, field.Number);
    }

    [Fact]
    public void PickingAValueIsOneWriteAndRemovingItBringsTheBoxBack()
    {
        var writes = new List<ParamValue?>();
        var field = Field(new ScalarParam("40"), writes);
        var exit = field.ValueGroups.SelectMany(g => g.Rows).First(r => r.Title == "Exit code");

        field.UseValue(exit);

        Assert.Single(writes);
        Assert.Equal("{ps.exitCode}", ((ScalarParam)writes[0]!).Text);
        Assert.True(field.UsesValue);
        Assert.False(field.ShowsNumberBox);

        field.RemovePill(field.Pills.First(p => p.IsPill));

        Assert.Equal(2, writes.Count);
        Assert.Null(writes[1]);
        Assert.False(field.UsesValue);
        Assert.True(field.ShowsNumberBox);
    }

    [Fact]
    public void ThePillsFallbackStaysAWholeValue()
    {
        var writes = new List<ParamValue?>();
        var field = Field(new ScalarParam("{ps.exitCode}"), writes);

        field.SetPillFallback(field.Pills.First(p => p.IsPill), "50");

        Assert.Equal("{ps.exitCode|50}", ((ScalarParam)writes[^1]!).Text);
        Assert.True(field.UsesValue);
    }

    [Fact]
    public void NoDevicesAreOfferedToANumber()
    {
        var field = Field(new ScalarParam("40"), []);

        var titles = field.ValueGroups.SelectMany(g => g.Rows).Select(r => r.Title).ToList();
        Assert.Contains("Exit code", titles);
        Assert.DoesNotContain("Device", titles);
        Assert.All(field.ValueGroups, g => Assert.NotEmpty(g.Rows));
    }

    [Fact]
    public void ADeviceInputIsNotOfferedButATextInputIs()
    {
        var flow = Flow with
        {
            Inputs = new(new Dictionary<string, FlowInput>
            {
                ["out"] = new(LibraryKind.Audio, "Output", "x"),
                ["note"] = new(null, "Note", "5"),
            }),
        };
        var spec = ProviderCatalog.Find("audio.setVolume")!.Params.First(p => p.Name == "percent");
        var field = new ParamFieldViewModel(spec, (_, _) => { }, _ => true);
        field.SetValueContext(flow, "vol");
        field.Load(new ScalarParam("40"), null);

        var titles = field.ValueGroups.SelectMany(g => g.Rows).Select(r => r.Stored).ToList();
        Assert.Contains("{$note}", titles);
        Assert.DoesNotContain("{$out}", titles);
    }

    [Fact]
    public void WithSeveralStepsSelectedTheBoxStaysAndNoButtonShows()
    {
        var field = Field(new ScalarParam("{ps.exitCode}"), [], context: false);

        Assert.False(field.UsesValue);
        Assert.False(field.ShowsValueButton);
        Assert.True(field.ShowsNumberBox);
    }
}
