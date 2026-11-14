using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Editor.ViewModels.Canvas;
using CtrlAltDaze.Editor.ViewModels.Config;
using CtrlAltDaze.Editor.ViewModels.Inspector;
using CtrlAltDaze.Editor.ViewModels.Runs;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>T14.7: the "Only if" row's choices, check wording, the chip, and how a skipped step looks after a run (D-081).</summary>
public sealed class ConditionsEditorTests : IDisposable
{
    private static readonly ConditionSpec[] Specs =
    [
        new("disconnected", "It isn't connected", "bluetooth.connected", false),
        new("connected", "It's connected", "bluetooth.connected", true),
    ];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CtrlAltDaze.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly InspectorViewModel _inspector;

    public ConditionsEditorTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
        _inspector = new InspectorViewModel(_session, _canvas, new FakeEngine { IsRunning = true }, action => action(), _ => true);
    }

    public void Dispose()
    {
        _inspector.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void RowChoicesComeFromTheConditionsAndAlwaysStoresNoKey()
    {
        Assert.Equal([InspectorViewModel.AlwaysText, "It isn't connected", "It's connected"], InspectorViewModel.OnlyChoices(Specs));
        Assert.Equal(0, InspectorViewModel.OnlyIndex(Specs, null));
        Assert.Equal(0, InspectorViewModel.OnlyIndex(Specs, StepNode.OnlyAlways));
        Assert.Equal(2, InspectorViewModel.OnlyIndex(Specs, "connected"));
        Assert.Null(InspectorViewModel.OnlyAt(Specs, 0));
        Assert.Equal("disconnected", InspectorViewModel.OnlyAt(Specs, 1));
    }

    [Fact]
    public void ATypeWithoutConditionsHasNoRowAndNoCheckWords()
    {
        _canvas.Select("w1");
        Assert.False(_inspector.HasOnlyIf);
        Assert.False(_inspector.IsCheck);
        Assert.Equal("If it fails", _inspector.PolicySectionTitle);
        Assert.Equal("If it fails, run…", _inspector.FallbackText);
    }

    [Fact]
    public void AnOnlyIfStepGetsAChipAndAlwaysDoesNot()
    {
        var bt = (StepNode)_canvas.Flow!.FindNode("bt")!;
        var node = new CanvasNodeViewModel("bt");
        node.Update(bt with { Only = "disconnected" }, _session.Current, false);
        Assert.Contains(node.Badges, b => b.Text == "Only if…" && b.Glyph == "");

        node.Update(bt with { Only = StepNode.OnlyAlways }, _session.Current, false);
        Assert.DoesNotContain(node.Badges, b => b.Text == "Only if…");
    }

    [Fact]
    public void ASkippedStepIsSucceededWithADashedRingAndACaption()
    {
        var view = RunWords.View(new NodeResult("bt", NodeOutcome.Succeeded) { SkippedBecause = "already connected" });
        Assert.Equal(NodeRunState.Succeeded, view.State);
        Assert.True(view.ConditionSkipped);
        Assert.Equal("Skipped: already connected", view.Text);

        var node = new CanvasNodeViewModel("bt");
        node.ShowRun(view);
        Assert.True(node.RunConditionSkipped);
        Assert.Equal("Skipped: already connected", node.ShownSummary);
        Assert.Contains("Skipped: already connected", node.AutomationName);

        node.ShowRun(RunWords.View(new NodeResult("bt", NodeOutcome.Succeeded, Duration: TimeSpan.FromSeconds(1))));
        Assert.False(node.RunConditionSkipped);
    }

    [Fact]
    public void TestThisStepAnswersYesOrNoForAChecksAndNamesASkip()
    {
        Assert.Equal("Yes: Technics is connected",
            InspectorViewModel.ExplainTest("bluetooth.check", new NodeResult("c", NodeOutcome.Succeeded) { Summary = "Technics is connected" }));
        Assert.Equal("No: Technics isn't connected",
            InspectorViewModel.ExplainTest("bluetooth.check", new NodeResult("c", NodeOutcome.Failed, Error: "Technics isn't connected\nmore")));
        Assert.Equal("Skipped: Technics was already connected",
            InspectorViewModel.ExplainTest("bluetooth.connect", new NodeResult("c", NodeOutcome.Succeeded) { SkippedBecause = "Technics was already connected" }));
        Assert.StartsWith("Worked", InspectorViewModel.ExplainTest("bluetooth.connect", new NodeResult("c", NodeOutcome.Succeeded)));
    }

    [Fact]
    public void AHandledCheckLeadsWithTheProbesWords()
    {
        var flow = _canvas.Flow!;
        var fix = ("bt", "out");
        var title = (string id) => id == "out" ? "Speakers" : id;
        // A normal step keeps the old sentence (#48).
        Assert.Equal("bt didn't work, so Speakers ran instead.", Outcomes.FixSentence(flow, [], fix, title));

        var checkFlow = flow with
        {
            Nodes = [new StepNode("bt", "bluetooth.check"), .. flow.Nodes.Where(n => n.Id != "bt")],
        };
        var failed = new NodeResult("bt", NodeOutcome.Failed, Error: "Technics isn't connected.");
        Assert.Equal("Technics isn't connected, so Speakers ran instead.", Outcomes.FixSentence(checkFlow, [failed], fix, title));
    }

    [Fact]
    public void PaletteSearchForCheckFindsTheCheckSteps()
    {
        var hits = StepPaletteViewModel.Search("check");
        Assert.Contains(hits, h => h.Step == "flag.check" && h.IsCheck);
        Assert.All(hits.Where(h => h.IsCheck), h => Assert.True(Outcomes.IsCheck(h.Step)));
    }
}
