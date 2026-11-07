using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

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
