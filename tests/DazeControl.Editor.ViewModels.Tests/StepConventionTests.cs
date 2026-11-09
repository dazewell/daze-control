using DazeControl.Core.Catalog;
using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>The step naming and palette conventions (D-102, docs/steps-review/REVIEW.md "Adding a step"): a new step that breaks one fails here.</summary>
public class StepConventionTests
{
    // A new verb goes in this list on purpose: it is the vocabulary users scan in the palette.
    private static readonly string[] Verbs =
    [
        "Set", "Check", "Connect", "Disconnect", "Project", "Use", "Restore", "Turn", "Run", "Close", "Send", "Start", "Stop", "Show",
        "Wait", "Choose", "Open", "Control", "Change", "Mute", "Wake", "Sleep", "Ask",
    ];

    [Fact]
    public void EveryTitleIsACapitalisedVerbThenABareObject()
    {
        foreach (var d in ProviderCatalog.All)
        {
            var words = d.Title.Split(' ');
            Assert.Contains(words[0], Verbs);
            Assert.DoesNotContain(words.Skip(1), w => w is "a" or "an" or "the");
            // Sentence case: only the first word and proper names (PowerShell, Android, Bluetooth) have capitals.
            Assert.All(words.Skip(1), w => Assert.True(!char.IsUpper(w[0]) || w is "PowerShell" or "Android" or "Bluetooth", $"{d.Id}: \"{d.Title}\""));
        }
    }

    [Fact]
    public void ChecksAreNamedCheckNounAndOnlyChecks()
    {
        foreach (var d in ProviderCatalog.All)
            Assert.Equal(d.Id.EndsWith(".check", StringComparison.Ordinal), d.Title.StartsWith("Check ", StringComparison.Ordinal));
    }

    [Fact]
    public void TitlesAreUniqueInsideTheirGroup()
    {
        var clashes = StepPaletteViewModel.All.GroupBy(i => (i.Area, i.Title)).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(clashes);
    }

    [Fact]
    public void EveryStepIsInAThemedGroupInFixedOrderWithChecksLast()
    {
        var all = StepPaletteViewModel.All;
        Assert.All(all, i => Assert.Contains(i.Area, CanvasNodeViewModel.Groups));
        var groupOrder = all.Select(i => CanvasNodeViewModel.Groups.ToList().IndexOf(i.Area)).ToList();
        Assert.Equal(groupOrder.OrderBy(x => x), groupOrder);
        foreach (var group in all.GroupBy(i => i.Area))
        {
            var items = group.ToList();
            var firstCheck = items.FindIndex(i => i.IsCheck);
            if (firstCheck >= 0)
                Assert.All(items.Skip(firstCheck), i => Assert.True(i.IsCheck, $"{group.Key}: {i.Title} comes after a Check"));
        }
    }

    [Fact]
    public void IdsAreAreaDotVerbInCamelCaseAndAndroidChoicesHaveCaptions()
    {
        foreach (var d in ProviderCatalog.All.Where(d => d.Id.Contains('.')))
            Assert.Matches("^[a-z]+\\.[a-z][A-Za-z]*$", d.Id);
        foreach (var d in ProviderCatalog.All.Where(d => d.Id.StartsWith("android.", StringComparison.Ordinal)))
            Assert.All(d.Params.Where(p => p.Kind == ParamKind.Choice), p => Assert.Equal(p.Choices.Count, p.Captions.Count));
    }
}
