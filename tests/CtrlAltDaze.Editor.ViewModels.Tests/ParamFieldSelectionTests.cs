using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Editor.ViewModels.Inspector;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

public sealed class ParamFieldSelectionTests
{
    private static ParamFieldViewModel Picker() =>
        new(new ParamSpec("device", ParamKind.LibraryRef, "Device") { Library = LibraryKind.Android }, (_, _) => { }, _ => true);

    private static DeviceOption Row(string key) => new(DeviceSource.Library, key, key, "", "");

    [Fact]
    public void RebuildingRowsRaisesDeviceAgainSoTheDropDownShowsTheValue()
    {
        var field = Picker();
        field.SetDevices([new DeviceOption(DeviceSource.None, null, "None", "", ""), Row("tv")], "tv");
        var raised = 0;
        field.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(ParamFieldViewModel.Device) ? 1 : 0;

        // The drop-down pushes null back when its rows are replaced; the value is still tv and must be read again.
        field.SetDevices([new DeviceOption(DeviceSource.None, null, "None", "", ""), Row("tv"), Row("phone")], "tv");

        Assert.Equal("tv", field.Device?.Key);
        Assert.True(raised >= 1);
    }

    [Fact]
    public void ReloadingTheSameValueStillRaisesDevice()
    {
        var field = Picker();
        field.SetDevices([Row("tv")], "tv");
        var raised = 0;
        field.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(ParamFieldViewModel.Device) ? 1 : 0;

        field.SetDevices([Row("tv")], "tv");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void KeySuggestionsWithEmptyQueryListEveryKey()
    {
        var spec = new ParamSpec("keys", ParamKind.TextList, "Keys") { Ordered = true, Suggest = new ParamSuggest(SuggestSource.Static, "keys") { Values = ["HOME", "BACK"] } };
        var field = new ParamFieldViewModel(spec, (_, _) => { }, _ => true);

        Assert.Equal(["HOME", "BACK"], field.KeySuggestions(""));
        Assert.Equal(["HOME"], field.KeySuggestions("ho"));
    }

    [Fact]
    public void EveryParamOfEveryStepLoadsWithoutWritingAndShowsItsValue()
    {
        var writes = new List<string>();
        foreach (var provider in ProviderCatalog.All)
            foreach (var spec in provider.Params)
            {
                var field = new ParamFieldViewModel(spec, (f, _) => writes.Add(provider.Id + "." + f.Name), _ => true);
                field.Load(null, null);
                field.Load(null, null, mixed: true);
                field.Load(spec.Choices.Count > 0 ? new ScalarParam(spec.Choices[0]) : new ScalarParam("1"), null);
                if (field.IsChoice)
                    Assert.Equal(spec.Choices[0], field.Choice);
                if (field.IsDevice)
                {
                    field.SetDevices([new DeviceOption(DeviceSource.None, null, "None", "", ""), Row("a")], "a");
                    field.Load(new ScalarParam("a"), null);
                    Assert.Equal("a", field.Device?.Key);
                }
            }

        // Showing a value is never an edit (no write loops, nothing dirty on a step switch).
        Assert.Empty(writes);
    }
}

public sealed class ChoiceCaptionTests
{
    private static IEnumerable<(ProviderDescriptor Provider, ParamSpec Spec)> ChoiceParams() =>
        ProviderCatalog.All.SelectMany(p => p.Params.Where(s => s.Kind == ParamKind.Choice).Select(s => (p, s)));

    [Fact]
    public void EveryChoiceWalksBetweenCaptionAndValue()
    {
        Assert.NotEmpty(ChoiceParams());
        foreach (var (provider, spec) in ChoiceParams())
        {
            var written = new List<ParamValue?>();
            var field = new ParamFieldViewModel(spec, (_, v) => written.Add(v), _ => true);
            field.Load(null, null);
            Assert.Equal(spec.Choices.Count, field.ChoiceOptions.Count);
            foreach (var value in spec.Choices)
            {
                var option = field.ChoiceOptions.Single(o => o.Value == value);
                Assert.Equal(spec.CaptionOf(value), option.Caption);
                Assert.False(string.IsNullOrWhiteSpace(option.Caption), provider.Id + "." + spec.Name);
                var before = written.Count;
                field.ChoiceOption = option;
                Assert.Equal(value, field.Choice);
                Assert.Same(option, field.ChoiceOption);
                // What is written is the value, never the caption (the default is left out).
                if (written.Count > before)
                    Assert.Equal(value == spec.Default ? null : value, (written[^1] as ScalarParam)?.Text);
            }
            // Loading a stored value selects its row at once.
            field.Load(new ScalarParam(spec.Choices[^1]), null);
            Assert.Equal(spec.Choices[^1], field.ChoiceOption?.Value);
        }
    }

    [Fact]
    public void MixedChoiceHasNoSelectionAndAMissingCaptionFallsBackToTheValue()
    {
        var spec = ProviderCatalog.Find("process.check")!.Params.Single(p => p.Name == "is");
        var field = new ParamFieldViewModel(spec, (_, _) => { }, _ => true);
        field.Load(null, null, mixed: true);
        Assert.Null(field.ChoiceOption);
        Assert.Equal("Mixed", field.Placeholder);

        var bare = new ParamSpec("x", ParamKind.Choice, "X") { Choices = ["a", "b"], Captions = ["Alpha"] };
        Assert.Equal("Alpha", bare.CaptionOf("a"));
        Assert.Equal("b", bare.CaptionOf("b"));
        Assert.Equal("zzz", bare.CaptionOf("zzz"));
    }

    [Fact]
    public void CardPillShowsTheCaption()
    {
        var d = ProviderCatalog.Find("process.check")!;
        var step = new StepNode("s", "process.check") { With = new([new("program", (ParamValue)"spotify"), new("is", (ParamValue)"notRunning")]) };
        Assert.Equal("spotify · Not running", CtrlAltDaze.Editor.ViewModels.Canvas.CanvasNodeViewModel.Summarize(step, d, new Configuration()));
    }
}
