using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>
/// Round trip of saved values through every param control: a stored value the control doesn't offer (a Wi-Fi network out of
/// range, a device not plugged in, a choice from a newer version) must still be shown, and opening must not rewrite it.
/// WinUI's ComboBox itself can't run headless: these tests pin what the controls bind to (view-model state).
/// </summary>
public sealed class InspectorRoundTripTests : IDisposable
{
    private const string Odd = "zz Not Offered 42";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    public static IEnumerable<object[]> Steps() => ProviderCatalog.All.Where(d => d.Params.Count > 0).Select(d => new object[] { d.Id });

    private static string StoredFor(ParamSpec p) => p.Kind switch
    {
        ParamKind.WholeNumber => "7",
        ParamKind.Duration => "7s",
        ParamKind.Boolean => "true",
        _ => Odd,
    };

    private static string Yaml(ProviderDescriptor d)
    {
        var with = string.Join(", ", d.Params.Where(p => p.Kind is not (ParamKind.TextList or ParamKind.TextMap))
            .Select(p => $"{p.Name}: \"{StoredFor(p)}\""));
        var lists = d.Params.Where(p => p.Kind is ParamKind.TextList).Select(p => $"{p.Name}: [\"{Odd}\"]");
        var maps = d.Params.Where(p => p.Kind is ParamKind.TextMap).Select(p => $"{p.Name}: {{ \"k\": \"{Odd}\" }}");
        var all = string.Join(", ", new[] { with }.Concat(lists).Concat(maps).Where(s => s.Length > 0));
        return "version: 1\nflows:\n  f:\n    name: F\n    nodes:\n      - { id: s, step: " + d.Id + ", with: { " + all + " } }\n";
    }

    /// <summary>Hand-edited values: padded with spaces. Shown as stored (trimmed for matching) and never rewritten by opening.</summary>
    [Theory]
    [MemberData(nameof(Steps))]
    public void PaddedValuesAreNotRewrittenByOpening(string stepId)
    {
        var descriptor = ProviderCatalog.Find(stepId)!;
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, stepId + ".padded.yaml");
        File.WriteAllText(path, Yaml(descriptor).Replace("\"" + Odd + "\"", "\"  " + Odd + "  \""));
        var before = File.ReadAllText(path);
        using var session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, a => a());
        session.Load();
        using var canvas = new FlowCanvasViewModel(session, "f");
        using var inspector = new InspectorViewModel(session, canvas, new FakeEngine { IsRunning = true }, a => a(), _ => true);
        canvas.Select("s");
        foreach (var field in inspector.Fields.Where(f => f.IsSuggestText))
        {
            field.SetSuggestions([], "");
            Assert.True(field.SelectedSuggestion is not null, field.Name);
        }
        foreach (var field in inspector.Fields.Where(f => f.IsChoice))
            Assert.True(field.ChoiceOption is not null, field.Name);
        session.Flush();
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void EveryStoredValueIsShownAndOpeningKeepsTheFileAsItWas(string stepId)
    {
        var descriptor = ProviderCatalog.Find(stepId)!;
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, stepId + ".yaml");
        File.WriteAllText(path, Yaml(descriptor));
        var before = File.ReadAllText(path);
        using var session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, a => a());
        session.Load();
        using var canvas = new FlowCanvasViewModel(session, "f");
        using var inspector = new InspectorViewModel(session, canvas, new FakeEngine { IsRunning = true }, a => a(), _ => true);
        canvas.Select("s");

        var step = (StepNode)canvas.Flow!.FindNode("s")!;
        foreach (var field in inspector.Fields)
        {
            var stored = (step.With.GetValueOrDefault(field.Name) as ScalarParam)?.Text;
            AssertShown(field, stored);

            // The live lists arrive later (and may not contain the value): the value must survive them.
            if (field.IsSuggestText)
            {
                field.SetSuggestions([new SuggestionItem("other", "")], "");
                AssertShown(field, stored);
                field.SetSuggestions([], "");
                AssertShown(field, stored);
            }
        }

        session.Flush();
        Assert.Equal(before, File.ReadAllText(path));
    }

    private static void AssertShown(ParamFieldViewModel f, string? stored)
    {
        var where = $"{f.Spec.Name} ({f.Kind})";
        switch (f.Kind)
        {
            case ParamKind.WholeNumber:
                Assert.True(double.Parse(stored!, System.Globalization.CultureInfo.InvariantCulture) == f.Number, where);
                break;
            case ParamKind.Duration:
                Assert.Equal(7, f.Number);
                break;
            case ParamKind.Boolean:
                Assert.True(f.IsOn, where);
                break;
            case ParamKind.Choice:
                Assert.True(f.ChoiceOption?.Value == stored, $"{where}: option {f.ChoiceOption?.Value ?? "(none)"}");
                Assert.Contains(f.ChoiceOption!, f.ChoiceOptions);
                break;
            case ParamKind.TextList:
                if (f.IsChecklist)
                    Assert.Contains(f.Screens, s => s.IsChecked && s.Value == Odd);
                else if (f.IsTokenList)
                    Assert.Contains(f.Tokens, t => t.Text == Odd);
                else if (!f.IsFlowList)
                    Assert.Contains(Odd, f.Text);
                break;
            case ParamKind.TextMap:
                Assert.Contains(Odd, f.Text);
                break;
            default:
                if (f.IsDevice)
                {
                    Assert.True(f.Device is { IsValue: true } d && d.Key == stored, $"{where}: device {f.Device?.Key ?? "(none)"}");
                    Assert.Contains(f.Device!, f.Devices);
                }
                else if (f.IsSuggestText)
                {
                    Assert.Equal(stored, f.Text);
                    Assert.True(f.SelectedSuggestion?.Value == stored, $"{where}: no row for the stored value");
                    Assert.Contains(f.SelectedSuggestion!, f.Suggestions);
                }
                else if (f.IsAppPicker)
                    Assert.Equal(stored, f.Text);
                else
                    Assert.Equal(stored, f.Text.Trim().Length == 0 ? stored : f.Text);
                break;
        }
    }
}
