using DazeControl.Core.Catalog;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T23.4 (D-129, DESIGN §5.3-5.6): pills, "Values it gives", and what delete, duplicate and paste do to references.</summary>
public sealed class StepValuesEditingTests : IDisposable
{
    private const string Yaml = """
        version: 1
        library:
          bluetooth: { technics: { address: "B8:20:8E:ED:48:FC", name: "Technics" } }
        flows:
          f:
            name: Connect Technics
            nodes:
              - { id: bt, step: bluetooth.connect, name: Connect Technics, with: { device: technics } }
              - { id: note, step: notify, with: { text: "Connected to {bt.device|unknown} at {@time}" }, after: [bt] }
              - { id: note2, step: notify, with: { text: "Again {bt.device}" }, after: [note] }
          g:
            name: Other
            nodes:
              - { id: x, step: wait, with: { duration: 1s } }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;

    public StepValuesEditingTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        Assert.True(_session.CanEdit, string.Join("; ", _session.Problems));
        _canvas = new FlowCanvasViewModel(_session, "f");
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static string TextOf(Flow flow, string id) => ((ScalarParam)((StepNode)flow.FindNode(id)!).With["text"]).Text;

    private static ParamSpec Spec(string name) => ProviderCatalog.Find("notify")!.Params.First(p => p.Name == name);

    private static ParamFieldViewModel Field(Flow flow, string stepId, string text, List<ParamValue?>? writes = null)
    {
        var field = new ParamFieldViewModel(Spec("text"), (_, v) => writes?.Add(v), _ => true);
        field.SetValueContext(flow, stepId);
        field.Load(new ScalarParam(text), null);
        return field;
    }

    [Fact]
    public void DeletingAProducingStepTurnsItsReferencesIntoTheirFallbackInOneUndo()
    {
        _canvas.SelectOnly(["bt"]);
        _canvas.DeleteSelection();

        Assert.Equal("Connected to unknown at {@time}", TextOf(_canvas.Flow!, "note"));
        Assert.Equal("Again ", TextOf(_canvas.Flow!, "note2"));

        _canvas.Undo();

        Assert.Equal("Connected to {bt.device|unknown} at {@time}", TextOf(_canvas.Flow!, "note"));
        Assert.Equal("Again {bt.device}", TextOf(_canvas.Flow!, "note2"));
        Assert.NotNull(_canvas.Flow!.FindNode("bt"));
    }

    [Fact]
    public void DeleteKeepsLiteralBracesAndOtherReferences()
    {
        var flow = new Flow("f", "F", [new StepNode("bt", "bluetooth.connect"),
            new StepNode("note", "notify") { With = new(new Dictionary<string, ParamValue> { ["text"] = new ScalarParam("{{bt.device} is {@time} {bt.device|x}") }), After = [new Edge("bt")] }]);

        var next = FlowCommands.Delete(flow, ["bt"]);

        Assert.Equal("{{bt.device} is {@time} x", TextOf(next, "note"));
    }

    [Fact]
    public void DuplicatingAStepWithItsProducerReadsTheCopy()
    {
        _canvas.SelectOnly(["bt", "note"]);
        _canvas.Duplicate();

        var flow = _canvas.Flow!;
        Assert.Equal("Connected to {bt-2.device|unknown} at {@time}", TextOf(flow, "note-2"));
        Assert.Equal("Connected to {bt.device|unknown} at {@time}", TextOf(flow, "note"));
    }

    [Fact]
    public void DuplicatingOnlyTheConsumerKeepsReadingTheOriginal()
    {
        _canvas.SelectOnly(["note2"]);
        _canvas.Duplicate();

        Assert.Equal("Again {bt.device}", TextOf(_canvas.Flow!, "note2-2"));
    }

    [Fact]
    public void PastingIntoAnotherFlowRemapsWhatCameAlongAndKeepsTheRest()
    {
        var flow = _canvas.Flow!;
        var other = _session.Current.Flows.First(f => f.Id == "g");
        // The other flow already has a step called "bt": the copy takes a new id, and its consumer follows it.
        var busy = other with { Nodes = [.. other.Nodes, new StepNode("bt", "wait")] };

        var (pasted, _) = FlowCommands.Paste(busy, FlowCommands.Copy(flow, ["bt", "note", "note2"]), after: null);
        var (alone, _) = FlowCommands.Paste(other, FlowCommands.Copy(flow, ["note2"]), after: null);

        Assert.Equal("Connected to {bt-2.device|unknown} at {@time}", TextOf(pasted, "note"));
        Assert.Equal("Again {bt.device}", TextOf(alone, "note2"));
    }

    [Fact]
    public void RewriteLeavesUntouchedTextTheSameInstance()
    {
        var text = "a {{b} c {@time}";

        Assert.Same(text, StepValues.Rewrite(text, _ => null));
        Assert.Equal("a {{b} c ", StepValues.Rewrite(text, _ => ""));
    }

    [Fact]
    public void PillsShowTheValueAndWhereItComesFromAndNameThemForNarrator()
    {
        var field = Field(_canvas.Flow!, "note", "Connected to {bt.device|unknown} at {@time}");

        Assert.True(field.HasPills);
        Assert.False(field.ShowsHelp);
        Assert.Equal(["Connected to ", "Device · Connect Technics", " at ", "Time · This run"], field.Pills.Select(p => p.Text));
        var device = field.Pills[1];
        Assert.True(device.IsPill);
        Assert.Equal("Device from Connect Technics, in Message. Press to change or remove.", device.AutomationName);
        Assert.Equal("unknown", device.Fallback);
        Assert.Equal("Shows: Connected to Device from Connect Technics at Time from This run", field.PillsName);
    }

    [Fact]
    public void NoValueKeepsTheRowAndShowsTheHelp()
    {
        var field = new ParamFieldViewModel(ProviderCatalog.Find("menu.show")!.Params.First(p => p.Name == "title"), (_, _) => { }, _ => true);
        field.SetValueContext(_canvas.Flow!, "note");
        field.Load(new ScalarParam("Hello"), null);

        Assert.True(field.TakesValues);
        Assert.False(field.HasPills);
        Assert.True(field.ShowsHelp);
        Assert.Empty(field.Pills);
    }

    [Fact]
    public void RemoveChangeAndFallbackAreOneWriteEach()
    {
        var writes = new List<ParamValue?>();
        var field = Field(_canvas.Flow!, "note", "Connected to {bt.device|unknown} at {@time}", writes);

        field.SetPillFallback(field.Pills[1], "nothing {x}\n");
        Assert.Equal("Connected to {Connect Technics: Device|nothing x} at {This run: Time}", field.Text);
        Assert.Single(writes);
        Assert.Equal("Connected to {bt.device|nothing x} at {@time}", ((ScalarParam)writes[0]!).Text);

        var date = field.ValueGroups.SelectMany(g => g.Rows).First(r => r.Title == "Date");
        field.ChangePill(field.Pills.Last(p => p.IsPill), date);
        Assert.Equal("Connected to {bt.device|nothing x} at {@date}", ((ScalarParam)writes[^1]!).Text);

        field.RemovePill(field.Pills.First(p => p.IsPill));
        Assert.Equal("Connected to  at {@date}", ((ScalarParam)writes[^1]!).Text);
        Assert.Equal(3, writes.Count);
    }

    [Fact]
    public void ReferenceToAStepThatIsGoneIsAPillWithAWarningThatCanBeRemoved()
    {
        var flow = _canvas.Flow!;
        var field = Field(flow with { Nodes = [.. flow.Nodes.Where(n => n.Id != "bt")] }, "note", "On {bt.device}");

        var pill = field.Pills.First(p => p.IsPill);
        Assert.True(pill.HasWarning);
        field.RemovePill(pill);
        Assert.DoesNotContain("bt.device", field.Text);
    }

    [Fact]
    public void ATypedTokenThatNamesNothingIsSaidSoUnderTheBox()
    {
        var field = Field(_canvas.Flow!, "note", "x");

        field.Text = "On {Connect Technics: Nope}";
        Assert.True(field.HasUnknownValue);
        Assert.False(field.HasPills);

        field.Text = "{\"a\": \"b\"}";
        Assert.False(field.HasUnknownValue);
    }

    [Fact]
    public void ValuesItGivesCountTheStepsThatUseThemAndNameTheCopyText()
    {
        var flow = _canvas.Flow!;

        var rows = InspectorViewModel.GivenValuesOf(flow, (StepNode)flow.FindNode("bt")!);

        var device = Assert.Single(rows);
        Assert.Equal("Device", device.Title);
        Assert.Equal("Used by 2 steps", device.UsedBy);
        Assert.Equal("{Connect Technics: Device}", device.CopyText);
        Assert.Contains("Press to copy", device.AutomationName, StringComparison.Ordinal);
        device.IsCopied = true;
        Assert.Equal("Copied", device.Note);
        Assert.Empty(InspectorViewModel.GivenValuesOf(flow, (StepNode)flow.FindNode("note")!));
    }

    [Fact]
    public void CanvasSummaryShowsTitlesNotIds()
    {
        var step = (StepNode)_canvas.Flow!.FindNode("note")!;

        var summary = CanvasNodeViewModel.Summarize(step, ProviderCatalog.Find("notify"), _session.Current);

        Assert.Equal("Connected to Device at Time", summary);
    }
}
