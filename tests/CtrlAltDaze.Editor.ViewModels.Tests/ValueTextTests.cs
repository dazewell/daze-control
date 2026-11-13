using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Editor.ViewModels.Inspector;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>D-129 / DESIGN §5: the display form of values in text, and the "Insert value" list.</summary>
public sealed class ValueTextTests
{
    private static readonly ValueList<ProviderDescriptor> Catalog = new(ProviderCatalog.All.Select(d => d.Id switch
    {
        "bluetooth.connect" => d with { Values = [new("device", ValueKind.Device, "Device") { Library = LibraryKind.Bluetooth, Help = "The device it connected" }] },
        "power.battery" => d with { Values = [new("percent", ValueKind.Number, "Battery")] },
        "notify" => d with { Params = [.. d.Params.Select(p => p.Name == "text" ? p with { TakesValues = true } : p)] },
        _ => d,
    }));

    private static StepNode Bt(string id, string? name, params Edge[] after) =>
        new(id, "bluetooth.connect") { Name = name, After = [.. after] };

    private static Flow FlowOf(params Node[] nodes) => new("f", "F", [.. nodes])
    {
        Inputs = new(new Dictionary<string, FlowInput> { ["output"] = new(LibraryKind.Audio, "Output", "x") }),
    };

    private static Flow Simple() => FlowOf(Bt("bt", "Connect Technics"), new StepNode("note", "notify") { After = [new Edge("bt")] });

    [Fact]
    public void StoredReferenceShowsStepNameAndValueTitle()
    {
        var shown = ValueText.ToDisplay(Simple(), "Connected to {bt.device} at {@time}", Catalog);

        Assert.Equal("Connected to {Connect Technics: Device} at {This run: Time}", shown);
        Assert.DoesNotContain("bt.", shown);
    }

    [Fact]
    public void DisplayFormGoesBackToTheStoredForm()
    {
        var flow = Simple();
        var stored = "A {bt.device|none}, {$output}, {@date}";

        var back = ValueText.FromDisplay(flow, ValueText.ToDisplay(flow, stored, Catalog), Catalog);

        Assert.Equal(stored, back);
    }

    [Fact]
    public void FallbackIsKeptInBothForms()
    {
        var flow = Simple();
        Assert.Equal("{Connect Technics: Device|unknown}", ValueText.ToDisplay(flow, "{bt.device|unknown}", Catalog));
        Assert.Equal("{bt.device|unknown}", ValueText.FromDisplay(flow, "{Connect Technics: Device|unknown}", Catalog));
    }

    [Fact]
    public void DuplicateNamesAreNumberedByFlowOrder()
    {
        var flow = FlowOf(Bt("a", "Connect"), Bt("b", "Connect", new Edge("a")), new StepNode("note", "notify") { After = [new Edge("b")] });

        Assert.Equal("{Connect: Device} {Connect (2): Device}", ValueText.ToDisplay(flow, "{a.device} {b.device}", Catalog));
        Assert.Equal("{a.device} {b.device}", ValueText.FromDisplay(flow, "{Connect: Device} {Connect (2): Device}", Catalog));
    }

    [Fact]
    public void RenamingAStepChangesTheDisplayNotTheStoredText()
    {
        var before = Simple();
        var after = before with { Nodes = [.. before.Nodes.Select(n => n.Id == "bt" ? n with { Name = "Technics" } : n)] };
        var shown = ValueText.ToDisplay(before, "{bt.device}", Catalog);

        Assert.Equal("{Technics: Device}", ValueText.ToDisplay(after, "{bt.device}", Catalog));
        // The old display text no longer names anything, so it stays as written.
        Assert.Equal("{Connect Technics: Device}", ValueText.FromDisplay(after, shown, Catalog));
        Assert.Equal("{bt.device}", ValueText.FromDisplay(after, ValueText.ToDisplay(after, "{bt.device}", Catalog), Catalog));
    }

    [Fact]
    public void DuplicateInputLabelsAreNumberedAndResolveToTheirOwnInput()
    {
        var flow = Simple() with
        {
            Inputs = new(new Dictionary<string, FlowInput>
            {
                ["a"] = new(LibraryKind.Audio, "Output", "x"),
                ["b"] = new(LibraryKind.Audio, "Output", "x"),
            }),
        };

        Assert.Equal("{Input: Output} {Input: Output (2)}", ValueText.ToDisplay(flow, "{$a} {$b}", Catalog));
        Assert.Equal("{$a} {$b}", ValueText.FromDisplay(flow, "{Input: Output} {Input: Output (2)}", Catalog));
    }

    [Fact]
    public void DelimitersInStepNamesStillRoundTrip()
    {
        var flow = FlowOf(Bt("bt", "A|B {x}"), new StepNode("note", "notify") { After = [new Edge("bt")] });

        var shown = ValueText.ToDisplay(flow, "{bt.device}", Catalog);

        Assert.Equal("{A B  x: Device}", shown);
        Assert.Equal("{bt.device}", ValueText.FromDisplay(flow, shown, Catalog));
    }

    [Fact]
    public void UnnamedStepShowsItsTypeTitle()
    {
        var flow = FlowOf(Bt("bt", null), new StepNode("note", "notify") { After = [new Edge("bt")] });
        var title = ProviderCatalog.Find("bluetooth.connect")!.Title;

        Assert.Equal($"{{{title}: Device}}", ValueText.ToDisplay(flow, "{bt.device}", Catalog));
    }

    [Fact]
    public void ReferencesThatNameNothingStayAsWritten()
    {
        var flow = Simple();

        Assert.Equal("{gone.device} {bt.nope}", ValueText.ToDisplay(flow, "{gone.device} {bt.nope}", Catalog));
        Assert.Equal("{gone.device} {bt.nope}", ValueText.FromDisplay(flow, "{gone.device} {bt.nope}", Catalog));
    }

    [Fact]
    public void HandTypedTokenThatMapsToNothingStaysPlainText()
    {
        var flow = Simple();

        var stored = ValueText.FromDisplay(flow, "Hi {Nobody: Device}", Catalog);

        Assert.Equal("Hi {Nobody: Device}", stored);
        Assert.Equal("Hi {Nobody: Device}", ValueText.ToDisplay(flow, stored, Catalog));
    }

    [Fact]
    public void HandTypedDisplayTokenThatMapsBecomesAReference()
    {
        Assert.Equal("On {bt.device}", ValueText.FromDisplay(Simple(), "On {Connect Technics: Device}", Catalog));
    }

    [Fact]
    public void DoubleBraceWritesADisplayTokenLiterally()
    {
        var flow = Simple();

        Assert.Equal("{Connect Technics: Device}", ValueText.FromDisplay(flow, "{{Connect Technics: Device}", Catalog));
        // And a literal that would read as a token is shown escaped, so it comes back as it was.
        Assert.Equal("{{Connect Technics: Device}", ValueText.ToDisplay(flow, "{Connect Technics: Device}", Catalog));
    }

    [Fact]
    public void StoredEscapePassesThroughBothWays()
    {
        var flow = Simple();

        Assert.Equal("{{bt.device} is {Connect Technics: Device}", ValueText.ToDisplay(flow, "{{bt.device} is {bt.device}", Catalog));
        Assert.Equal("{{bt.device} is {bt.device}", ValueText.FromDisplay(flow, "{{bt.device} is {Connect Technics: Device}", Catalog));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("{ exit 1 }")]
    [InlineData("%TEMP% {\"a\":1}")]
    public void PlainTextIsUntouched(string text)
    {
        var flow = Simple();

        Assert.Equal(text, ValueText.ToDisplay(flow, text, Catalog));
        Assert.Equal(text, ValueText.FromDisplay(flow, text, Catalog));
    }

    [Fact]
    public void InsertReplacesTheSelectionAndMovesTheCaretAfterIt()
    {
        var text = ValueText.Insert("Hello XX!", 6, 2, "{This run: Time}", out var caret);

        Assert.Equal("Hello {This run: Time}!", text);
        Assert.Equal(6 + "{This run: Time}".Length, caret);
        Assert.Equal("{A}", ValueText.Insert("", 5, 3, "{A}", out _));
    }

    [Fact]
    public void ListHasEarlierStepsThenInputsThenTheRun()
    {
        var flow = FlowOf(Bt("bt", "Connect Technics"), new StepNode("batt", "power.battery") { After = [new Edge("bt")] },
            new StepNode("note", "notify") { After = [new Edge("batt")] });

        var groups = ValueText.Choices(flow, "note", Catalog);

        Assert.Equal(["Connect Technics", ProviderCatalog.Find("power.battery")!.Title, ValueText.FromInputs, ValueText.FromRun], groups.Select(g => g.Header));
        Assert.Equal([ValueText.FromSteps, ValueText.FromSteps, ValueText.FromInputs, ValueText.FromRun], groups.Select(g => g.Section));
        Assert.Equal(["{bt.device}"], groups[0].Rows.Select(r => r.Stored));
        Assert.Equal("{Connect Technics: Device}", groups[0].Rows[0].Display);
        Assert.Equal("Device. The device it connected", groups[0].Rows[0].AutomationName);
        Assert.Equal(["{$output}"], groups[2].Rows.Select(r => r.Stored));
        Assert.Equal(["Flow name", "What started it", "Device or network", "Time", "Date"], groups[3].Rows.Select(r => r.Title));
        Assert.Equal(["{@flow}", "{@trigger}", "{@event}", "{@time}", "{@date}"], groups[3].Rows.Select(r => r.Stored));
    }

    [Fact]
    public void OnlyStepsItWaitsForAreListed()
    {
        var flow = FlowOf(Bt("bt", "A"), Bt("other", "B"), new StepNode("note", "notify") { After = [new Edge("bt")] });

        Assert.DoesNotContain(ValueText.Choices(flow, "note", Catalog), g => g.Header == "B");
        Assert.Contains(ValueText.Choices(flow, "note", Catalog), g => g.Header == "A");
    }

    [Fact]
    public void ErrorIsOfferedOnlyAfterAFailureEdge()
    {
        var onSuccess = FlowOf(Bt("bt", "A"), new StepNode("note", "notify") { After = [new Edge("bt")] });
        var onFailure = FlowOf(Bt("bt", "A"), new StepNode("note", "notify") { After = [new Edge("bt", EdgeWhen.Failure)] });

        Assert.DoesNotContain(ValueText.Choices(onSuccess, "note", Catalog).SelectMany(g => g.Rows), r => r.Stored == "{bt.error}");
        Assert.Contains(ValueText.Choices(onFailure, "note", Catalog).SelectMany(g => g.Rows), r => r.Stored == "{bt.error}");
    }

    [Fact]
    public void FirstStepWithoutInputsHasOnlyRunValues()
    {
        var flow = new Flow("f", "F", [new StepNode("note", "notify")]);

        Assert.Equal([ValueText.FromRun], ValueText.Choices(flow, "note", Catalog).Select(g => g.Header));
    }

    [Fact]
    public void FieldShowsDisplayFormAndWritesStoredForm()
    {
        var flow = Simple();
        ParamValue? written = null;
        var spec = Catalog.First(d => d.Id == "notify").Params.First(p => p.Name == "text");
        var field = new ParamFieldViewModel(spec, (_, v) => written = v, _ => true);
        field.SetValueContext(flow, "note", Catalog);
        field.Load(new ScalarParam("On {bt.device}"), null);

        Assert.Equal("On {Connect Technics: Device}", field.Text);
        Assert.True(field.TakesValues);
        Assert.Equal("Insert a value into " + spec.Title, field.InsertValueName);

        var time = field.ValueGroups[^1].Rows.First(r => r.Title == "Time");
        var caret = field.InsertValue(time, field.Text.Length, 0);

        Assert.Equal(new ScalarParam("On {bt.device}{@time}"), written);
        Assert.Equal(field.Text.Length, caret);
    }

    [Fact]
    public void FieldWithoutContextTakesNoValuesAndKeepsTheText()
    {
        var spec = Catalog.First(d => d.Id == "notify").Params.First(p => p.Name == "text");
        var field = new ParamFieldViewModel(spec, (_, _) => { }, _ => true);
        field.Load(new ScalarParam("On {bt.device}"), null);

        Assert.False(field.TakesValues);
        Assert.Equal("On {bt.device}", field.Text);
        Assert.Empty(field.ValueGroups);
    }

    [Fact]
    public void RealCatalogShowMessageAfterBluetoothConnectListsTheDevice()
    {
        var flow = new Flow("f", "F", [new StepNode("bt", "bluetooth.connect") { Name = "Connect Technics" },
            new StepNode("note", "notify") { After = [new Edge("bt")] }]);
        var spec = ProviderCatalog.Find("notify")!.Params.First(p => p.Name == "text");
        var field = new ParamFieldViewModel(spec, (_, _) => { }, _ => true);
        field.SetValueContext(flow, "note");
        field.Load(new ScalarParam("On {bt.device}"), null);

        Assert.True(field.TakesValues);
        Assert.Equal("On {Connect Technics: Device}", field.Text);
        Assert.Equal(["Connect Technics", ValueText.FromRun], field.ValueGroups.Select(g => g.Header));
        Assert.Contains(field.ValueGroups[0].Rows, r => r.Stored == "{bt.device}");
    }
}
