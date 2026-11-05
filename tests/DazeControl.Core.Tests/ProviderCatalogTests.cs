using System.Globalization;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;

namespace DazeControl.Core.Tests;

public class ProviderCatalogTests
{
    public static TheoryData<string> Ids() => [.. ProviderCatalog.All.Select(d => d.Id)];

    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(ProviderCatalog.All.Count, ProviderCatalog.All.Select(d => d.Id).Distinct().Count());

    [Fact]
    public void CoversTheUsersFlow()
    {
        Assert.NotNull(ProviderCatalog.Find("bluetooth.connect"));
        Assert.NotNull(ProviderCatalog.Find("audio.setDefault"));
        Assert.Null(ProviderCatalog.Find("bluetooth"));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void DescriptorIsWellFormed(string id)
    {
        var descriptor = ProviderCatalog.Find(id)!;

        Assert.Matches("^[a-z]+(\\.[a-zA-Z]+)?$", descriptor.Id);
        Assert.Matches("^E[0-9A-F]{3}$", descriptor.Glyph);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Title));
        Assert.True(descriptor.DefaultTimeout > TimeSpan.Zero);
        Assert.Equal(descriptor.Params.Count, descriptor.Params.Select(p => p.Name).Distinct().Count());
        foreach (var resource in descriptor.Resources)
        {
            foreach (var placeholder in System.Text.RegularExpressions.Regex.Matches(resource, "\\{(\\w+)\\}").Select(m => m.Groups[1].Value))
                Assert.True(descriptor.FindParam(placeholder) is { Required: true }, $"{id}: resource {resource} needs required param {placeholder}");
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void ParamsAreConsistentWithTheirKind(string id)
    {
        foreach (var param in ProviderCatalog.Find(id)!.Params)
        {
            var where = $"{id}.{param.Name}";
            Assert.Equal(param.Kind == ParamKind.Choice, param.Choices.Count > 0);
            Assert.Equal(param.Kind == ParamKind.LibraryRef, param.Library is not null);
            Assert.False(param.Required && param.Default is not null, $"{where}: required params have no default");
            if (param.Default is not { } value)
                continue;
            var ok = param.Kind switch
            {
                ParamKind.Choice => param.Choices.Contains(value),
                ParamKind.Boolean => bool.TryParse(value, out _),
                ParamKind.WholeNumber => int.TryParse(value, CultureInfo.InvariantCulture, out _),
                ParamKind.Duration => Duration.TryParse(value, out _),
                _ => true,
            };
            Assert.True(ok, $"{where}: default \"{value}\" doesn't fit {param.Kind}");
        }
    }

    [Fact]
    public void AreaIsThePartBeforeTheDot()
    {
        Assert.Equal("bluetooth", ProviderCatalog.Find("bluetooth.connect")!.Area);
        Assert.Equal("wait", ProviderCatalog.Find("wait")!.Area);
    }

    /// <summary>Free text the user is fine typing (reviewed 2026-11-02, D-050): a message, a program's own arguments.</summary>
    private static readonly HashSet<string> TypedFreely = ["notify.text", "menu.show.title","process.run.args", "process.run.exitCodes"];

    [Fact]
    public void EveryFreeTextParamSaysWhereItsValuesComeFrom()
    {
        var bare = ProviderCatalog.All
            .SelectMany(d => d.Params.Select(p => (Path: $"{d.Id}.{p.Name}", Param: p)))
            .Where(x => x.Param.Kind is ParamKind.Text or ParamKind.TextList or ParamKind.MultilineText or ParamKind.TextMap)
            .Where(x => x.Param.Suggest is null && x.Param.Examples.Count == 0 && !TypedFreely.Contains(x.Path))
            .Select(x => x.Path);

        Assert.Empty(bare);
    }

    [Fact]
    public void SuggestionsAreWellFormed()
    {
        foreach (var param in ProviderCatalog.All.SelectMany(d => d.Params))
        {
            if (param.Suggest is { } suggest)
            {
                Assert.NotEmpty(suggest.Kind);
                Assert.Equal(suggest.Source == SuggestSource.Static, suggest.Values.Count > 0);
            }
            Assert.All(param.Examples, e => Assert.False(string.IsNullOrWhiteSpace(e.Title) || string.IsNullOrWhiteSpace(e.Value)));
            if (param.Ordered)
                Assert.Equal(ParamKind.TextList, param.Kind);
        }
    }

    [Fact]
    public void AndroidKeysAreSentInOrderFromTheSharedTable()
    {
        var keys = ProviderCatalog.Find("android.keys")!.Params.Single(p => p.Name == "keys");

        Assert.True(keys.Ordered);
        Assert.Equal(AndroidKeys.Names, keys.Suggest!.Values);
        Assert.Equal("KEYCODE_HOME", AndroidKeys.Code("Home"));
        Assert.Null(AndroidKeys.Code("teleport"));
    }
}
