using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>Step values (D-129, docs/step-values/DESIGN.md §4): grammar, the §4.6 checks, fill, run values.</summary>
public class StepValuesTests
{
    /// <summary>The compiled-in catalog with T23.2's first values and settings marked, so the checks have something to see.</summary>
    private static readonly ValueList<ProviderDescriptor> Catalog = new(ProviderCatalog.All.Select(d => d.Id switch
    {
        "bluetooth.connect" => d with { Values = [new("device", ValueKind.Device, "Device") { Library = LibraryKind.Bluetooth }] },
        "power.battery" => d with { Values = [new("percent", ValueKind.Number, "Battery (%)")] },
        "script.powershell" => d with { Values = [new("exitCode", ValueKind.Number, "Exit code") { OnFailure = true }] },
        "prompt.pick" => d with { Values = [new("device", ValueKind.Device, "Device")] },
        "notify" => d with { Params = [.. d.Params.Select(p => p.Name == "text" ? p with { TakesValues = true } : p)] },
        _ => d,
    }));

    private const string Header = """
        library:
          audio:
            technics: { match: Technics }
          bluetooth:
            sony: { address: "B8:20:8E:ED:48:FC", name: Sony WH }
          bound:
            buds: { bluetooth: sony, audio: technics, name: Galaxy Buds }

        """;

    private static string Yaml(string nodes, string extra = "") => Header + extra + """
        flows:
          f:
            name: F
            inputs:
              output: { kind: audio, name: Output, default: technics }
            nodes:

        """ + nodes;

    private const string Bt = "      - { id: bt, step: bluetooth.connect, with: { device: sony } }\n";

    private static Configuration Load(string yaml)
    {
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private static List<Problem> Problems(string yaml) => [.. ConfigValidator.Validate(Load(yaml), catalog: Catalog)];

    private static Problem Only(string yaml, Severity severity)
    {
        var problems = Problems(yaml);
        return Assert.Single(problems, p => p.Severity == severity);
    }

    private static string Note(string text, string after = "bt") =>
        $"      - {{ id: note, step: notify, after: [{after}], with: {{ text: \"{text}\" }} }}\n";

    // ---- Grammar ----

    [Fact]
    public void ParsesStepInputAndRunReferencesWithFallbacksAndPositions()
    {
        var refs = StepValues.Parse("Connected to {bt.device} at {@time}. Battery {batt.percent|unknown}% on {$output}");
        Assert.Equal(4, refs.Count);
        Assert.Equal(new ValueRef(ValueSource.Step, "bt", "device", null, 13, 11), refs[0]);
        Assert.Equal(new ValueRef(ValueSource.Run, "time", "", null, 28, 7), refs[1]);
        Assert.Equal(("batt", "percent", "unknown"), (refs[2].Name, refs[2].Value, refs[2].Fallback));
        Assert.Equal((ValueSource.Input, "output"), (refs[3].Source, refs[3].Name));
        Assert.Equal("{batt.percent|unknown}", refs[2].ToString());
    }

    [Theory]
    [InlineData("""{ "key": "value" }""")]
    [InlineData("""{"a":1,"b":{"c":2}}""")]
    [InlineData("if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { exit 1 }")]
    [InlineData("Get-ChildItem | ForEach-Object {$_.Name}")]
    [InlineData("$h = @{x=1}; ${env:TEMP}")]
    [InlineData("%TEMP%\\logs\\{date}.txt")]
    [InlineData("%USERPROFILE%\\Music")]
    [InlineData("{bt.Device} {-bt.device} {bt.device.name} {bt.} {.device} {$Out} {$_} {@} {bt device}")]
    [InlineData("{bt.device|a{b}} {bt.device|line\nbreak}")]
    [InlineData("{{ ok }} and {{bt.device}")]
    public void TextThatIsNoReferenceShapeIsPlain(string text) => Assert.Empty(StepValues.Parse(text));

    [Theory]
    [InlineData("{a-b_c.v}", "a-b_c", "v")]
    [InlineData("{9.exitCode}", "9", "exitCode")]
    [InlineData("{Bt.device|}", "Bt", "device")]
    public void StepIdsAndValueNamesFollowTheirRules(string text, string step, string value)
    {
        var r = Assert.Single(StepValues.Parse(text));
        Assert.Equal((step, value), (r.Name, r.Value));
    }

    [Fact]
    public void AnEmptyFallbackIsStillAFallback() => Assert.Equal("", Assert.Single(StepValues.Parse("{bt.device|}")).Fallback);

    [Fact]
    public void InputNamesFollowD064AndRunNamesAreWords()
    {
        Assert.Equal("out-2", Assert.Single(StepValues.Parse("{$out-2}")).Name);
        Assert.Equal("time", Assert.Single(StepValues.Parse("{@time|now}")).Name);
    }

    [Fact]
    public void DoubleBraceWritesAReferenceLiterally()
    {
        var parts = StepValues.Split("Type {{bt.device} to show {bt.device}, {{ ok }}");
        Assert.Equal(["Type {bt.device} to show ", "{bt.device}", ", {{ ok }}"], parts.Select(p => p.Text));
        Assert.Equal([false, true, false], parts.Select(p => p.Ref is not null));
    }

    [Theory]
    [InlineData("{bt.device}")]
    [InlineData("{{bt.device}")]
    [InlineData("a {$x} b {@time|y} {{{ c")]
    [InlineData("{ exit 1 }")]
    public void EscapeGivesThePlainTextBack(string literal)
    {
        var stored = StepValues.Escape(literal);
        Assert.Empty(StepValues.Parse(stored));
        Assert.Equal(literal, string.Concat(StepValues.Split(stored).Select(p => p.Text)));
    }

    // ---- §4.6 ----

    [Fact]
    public void AReferenceAfterItsStepIsClean() => Assert.Empty(Problems(Yaml(Bt + Note("Connected to {bt.device}"))));

    [Fact]
    public void AnUnknownStepOnlyWarnsAndShowsAsWritten()
    {
        var p = Only(Yaml(Bt + Note("{bt2.device}")), Severity.Warning);
        Assert.Equal("There's no step called \"bt2\" in this flow, so {bt2.device} shows as written.", p.Message);
        Assert.Equal("flows.f.nodes.note.with.text", p.Path);
    }

    [Fact]
    public void AKnownStepWithAnUnknownValueIsAnError() =>
        Assert.Equal("Connect gives no value called \"nmae\" (it gives Device, Why it failed).",
            Only(Yaml(Bt + Note("{bt.nmae}")), Severity.Error).Message);

    [Fact]
    public void AStepThatDoesNotAlwaysRunBeforeIsAnError() =>
        Assert.Equal("\"Connect\" must run before this step: connect it so this step comes after.",
            Only(Yaml(Bt + "      - { id: note, step: notify, with: { text: \"{bt.device}\" } }\n"), Severity.Error).Message);

    [Fact]
    public void AStepCannotUseItsOwnValues() =>
        Assert.Equal("A step can't use its own values: {note.error}.",
            Only(Yaml(Bt + Note("{note.error}")), Severity.Error).Message);

    [Fact]
    public void ACardSiblingStartsTogetherSoItsValuesAreNotReady()
    {
        var yaml = Yaml("""
                  - { id: c, group: [bt, note] }
                  - { id: bt, step: bluetooth.connect, with: { device: sony } }
                  - { id: note, step: notify, with: { text: "{bt.device}" } }
            """);
        Assert.Contains("starts together with this step (same card)", Only(yaml, Severity.Error).Message);
    }

    [Fact]
    public void AMemberOfACardItWaitsForIsBeforeAndSure()
    {
        var yaml = Yaml("""
                  - { id: c, group: [bt, batt] }
                  - { id: bt, step: bluetooth.connect, with: { device: sony } }
                  - { id: batt, step: power.battery }
                  - { id: note, step: notify, after: [c], with: { text: "{bt.device} {batt.percent}" } }
            """);
        Assert.Empty(Problems(yaml));
    }

    [Fact]
    public void ACardThatRunsTheNextStepsAnywayMakesItsMembersValuesMayBeMissing()
    {
        var yaml = Yaml("""
                  - { id: c, group: [bt, batt], onFailure: ignore }
                  - { id: bt, step: bluetooth.connect, with: { device: sony } }
                  - { id: batt, step: power.battery }
                  - { id: note, step: notify, after: [c], with: { text: "{bt.device}" } }
            """);
        Assert.Contains("(Run the next steps anyway)", Only(yaml, Severity.Warning).Message);
    }

    [Fact]
    public void ThroughOtherStepsIsBefore() =>
        Assert.Empty(Problems(Yaml(Bt + "      - { id: w, step: wait, after: [bt], with: { duration: 1s } }\n" + Note("{bt.device}", "w"))));

    [Fact]
    public void RunNextStepsAnywayMayLeaveItEmptyUnlessAFallbackIsWritten()
    {
        const string ignoring = "      - { id: bt, step: bluetooth.connect, onFailure: ignore, with: { device: sony } }\n";
        Assert.Equal("\"Connect\" may not give it (Run the next steps anyway), so Device can be empty. Add what to show when it has no value.",
            Only(Yaml(ignoring + Note("{bt.device}")), Severity.Warning).Message);
        Assert.Empty(Problems(Yaml(ignoring + Note("{bt.device|nothing}"))));
        Assert.Empty(Problems(Yaml(ignoring + Note("{bt.device|}"))));
    }

    [Fact]
    public void ATypeDefaultPolicyCounts()
    {
        const string defaults = "settings:\n  stepDefaults:\n    bluetooth.connect: { onFailure: ignore }\n";
        Assert.Contains("(Run the next steps anyway)", Only(Yaml(Bt + Note("{bt.device}"), defaults), Severity.Warning).Message);
    }

    [Fact]
    public void AnOnlyIfMayLeaveItEmpty()
    {
        var yaml = Yaml("      - { id: bt, step: bluetooth.connect, only: notConnected, with: { device: sony } }\n" + Note("{bt.device}"));
        Assert.Contains("may be skipped (Only if)", Only(yaml, Severity.Warning).Message);
        Assert.Empty(Problems(Yaml("      - { id: bt, step: bluetooth.connect, only: always, with: { device: sony } }\n" + Note("{bt.device}"))));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("always")]
    public void AFailureOrAlwaysEdgeMayLeaveItEmpty(string when)
    {
        var yaml = Yaml(Bt + $"      - {{ id: note, step: notify, after: [{{ id: bt, when: {when} }}], with: {{ text: \"{{bt.device}}\" }} }}\n");
        Assert.Contains("may fail or be skipped on the way to this step", Only(yaml, Severity.Warning).Message);
    }

    [Fact]
    public void WhyItFailedIsSureOnlyOnAFailureEdge()
    {
        var onFailure = Yaml(Bt + "      - { id: note, step: notify, after: [{ id: bt, when: failure }], with: { text: \"Couldn't: {bt.error}\" } }\n");
        Assert.Empty(Problems(onFailure));
        Assert.Equal("Why it failed has a value only when \"Connect\" fails. Add what to show when it has no value.",
            Only(Yaml(Bt + Note("{bt.error}")), Severity.Warning).Message);
    }

    [Fact]
    public void AnOnFailureValueIsSureWheneverTheStepRan()
    {
        var yaml = Yaml("""
                  - { id: s, step: script.powershell, with: { script: "exit 3" } }
                  - { id: note, step: notify, after: [{ id: s, when: failure }], with: { text: "Exit {s.exitCode}" } }
                  - { id: n2, step: notify, after: [{ id: s, when: always }], with: { text: "Exit {s.exitCode}" } }
            """);
        var warning = Assert.Single(Problems(yaml));
        Assert.Equal("flows.f.nodes.n2.with.text", warning.Path);
    }

    [Fact]
    public void AFallbackStepReachedBySuccessEdgesGivesSureValues()
    {
        var yaml = Yaml("""
                  - { id: a, step: wait, with: { duration: 1s } }
                  - { id: bt, step: bluetooth.connect, after: [{ id: a, when: failure }], with: { device: sony } }
                  - { id: note, step: notify, after: [bt], with: { text: "{bt.device}" } }
            """);
        Assert.Empty(Problems(yaml));
    }

    [Fact]
    public void AJoinAfterAStepAndItsFallbackMayRunWithoutTheFallback()
    {
        // note runs when a succeeds (bt ends NotNeeded) or when a fails and bt runs (D-112): bt's values may be missing.
        var yaml = Yaml("""
                  - { id: a, step: wait, with: { duration: 1s } }
                  - { id: bt, step: bluetooth.connect, after: [{ id: a, when: failure }], with: { device: sony } }
                  - { id: note, step: notify, after: [a, bt], with: { text: "{bt.device}" } }
                  - { id: n2, step: notify, after: [bt, a], with: { text: "{bt.device}" } }
            """);
        Assert.Equal(["flows.f.nodes.note.with.text", "flows.f.nodes.n2.with.text"],
            Problems(yaml).Where(p => p.Severity == Severity.Warning).Select(p => p.Path));
    }

    [Fact]
    public void AFailureEdgeInAJoinIsNotSure()
    {
        // When bt is skipped by its Only if, its failure edge doesn't fire, and the join can still start through w.
        var yaml = Yaml("""
                  - { id: bt, step: bluetooth.connect, only: notConnected, with: { device: sony } }
                  - { id: w, step: wait, with: { duration: 1s } }
                  - { id: note, step: notify, after: [{ id: bt, when: failure }, w], with: { text: "{bt.error}" } }
            """);
        Assert.Contains("has a value only when", Only(yaml, Severity.Warning).Message);
    }

    [Fact]
    public void LaterSuccessEdgesKeepAFailureEdgesFacts()
    {
        var yaml = Yaml(Bt + """
                  - { id: w, step: wait, after: [{ id: bt, when: failure }], with: { duration: 1s } }
                  - { id: note, step: notify, after: [w], with: { text: "{bt.error}" } }
            """);
        Assert.Empty(Problems(yaml));
    }

    [Fact]
    public void ASettingThatTakesNoValuesWarnsOnlyForRealReferences()
    {
        var yaml = Yaml(Bt + """
                  - { id: s, step: script.powershell, after: [bt], with: { script: "Write-Host {bt.device}; {bt2.device}; ForEach-Object {$_}" } }
            """);
        var p = Only(yaml, Severity.Warning);
        Assert.Equal("This setting doesn't take values; {bt.device} is used as written.", p.Message);
        Assert.Equal("flows.f.nodes.s.with.script", p.Path);
    }

    [Theory]
    [InlineData("audio.setVolume", "percent", "Volume (%)")]
    [InlineData("audio.setDefault", "device", "Audio device")]
    public void AReferenceInANumberOrDeviceSettingIsOneError(string type, string setting, string title)
    {
        var yaml = Yaml(Bt + $"      - {{ id: x, step: {type}, after: [bt], with: {{ {setting}: \"{{bt.device}}\" }} }}\n");
        Assert.Equal($"Values from earlier steps only go in text settings; {title} can't use {{bt.device}}.", Only(yaml, Severity.Error).Message);
    }

    [Fact]
    public void UnknownInputsAndRunValuesAreErrors()
    {
        var errors = Problems(Yaml("      - { id: note, step: notify, with: { text: \"{$nope} {@nope} {$output} {@flow} {@trigger} {@time} {@date}\" } }\n"))
            .Where(p => p.Severity == Severity.Error).Select(p => p.Message).ToList();
        Assert.Equal(["This flow has no input called \"nope\".", "There's no run value called \"nope\" (flow, trigger, time, date)."], errors);
    }

    [Fact]
    public void ResultReferencesBehaveAsBefore()
    {
        var yaml = Yaml("""
                  - { id: pick, step: prompt.pick, with: { kind: audio } }
                  - { id: set, step: audio.setDefault, after: [pick], with: { device: $result:pick } }
                  - { id: early, step: audio.setDefault, with: { device: $result:pick } }
            """);
        var error = Only(yaml, Severity.Error);
        Assert.Equal("flows.f.nodes.early.with.device", error.Path);
        Assert.Equal("\"Pick device\" must run before this step: connect it so this step comes after.", error.Message);
        Assert.Equal(["pick"], PickResults.Before(Load(yaml).Flows[0], "set").Select(s => s.Id));
    }

    [Fact]
    public void ADeviceValueMustFitTheDeviceSetting()
    {
        var flow = Load(Yaml("""
                  - { id: bt, step: bluetooth.connect, with: { device: sony } }
                  - { id: bound, step: prompt.pick, with: { kind: bound } }
                  - { id: batt, step: power.battery }
                  - { id: x, step: audio.setDefault, after: [bt, bound, batt], with: { device: technics } }
            """)).Flows[0];
        Assert.Equal("Device of \"Connect\" is a Bluetooth device; this setting needs an audio device.",
            StepValues.DeviceProblem(flow, "x", "bt", "device", LibraryKind.Audio, Catalog));
        Assert.Null(StepValues.DeviceProblem(flow, "x", "bound", "device", LibraryKind.Audio, Catalog));
        Assert.Equal("Battery (%) of \"Check battery\" isn't a device; this setting needs an audio device.",
            StepValues.DeviceProblem(flow, "x", "batt", "percent", LibraryKind.Audio, Catalog));
        Assert.Equal("\"Pick device\" must run before this step: connect it so this step comes after.",
            StepValues.DeviceProblem(flow, "bt", "bound", "device", LibraryKind.Bluetooth, Catalog));
    }

    [Fact]
    public void TheCompiledInCatalogMarksOnlyTextSettings()
    {
        foreach (var spec in ProviderCatalog.All.SelectMany(d => d.Params).Where(p => p.TakesValues))
            Assert.True(StepValues.Takes(spec), spec.Name);
        foreach (var value in ProviderCatalog.All.SelectMany(d => d.Values))
            Assert.Matches("^[a-z][A-Za-z0-9]*$", value.Name);
    }

    // ---- Fill, run values ----

    private static (Flow Flow, Library Library) Sample()
    {
        var config = Load(Yaml(Bt + "      - { id: batt, step: power.battery }\n" + Note("{bt.device} {batt.percent|?}% {x.y} {{bt.device}", "bt, batt")));
        return (config.Flows[0], config.Library);
    }

    [Fact]
    public void FillShowsNamesFallbacksAndLeavesUnknownStepsAsWritten()
    {
        var (flow, library) = Sample();
        var values = new RunValues();
        values.SetDevice("bt", "device", "sony", LibraryKind.Bluetooth);
        var note = (StepNode)flow.FindNode("note")!;
        var filled = StepValues.Fill(flow, note, values, library, out var missing, Catalog.First(d => d.Id == "notify"));
        Assert.Null(missing);
        Assert.Equal("Sony WH ?% {x.y} {bt.device}", ((ScalarParam)filled.With["text"]).Text);
        values.SetNumber("batt", "percent", 35L);
        Assert.Equal("Sony WH 35% {x.y} {bt.device}", StepValues.FillText(flow, ((ScalarParam)note.With["text"]).Text, values, library));
    }

    [Fact]
    public void FillLeavesSettingsThatTakeNoValuesAlone()
    {
        var (flow, library) = Sample();
        var script = new StepNode("s", "script.powershell") { With = new([KeyValuePair.Create("script", (ParamValue)"echo {bt.device}")]) };
        var values = new RunValues();
        values.SetDevice("bt", "device", "sony");
        Assert.Same(script, StepValues.Fill(flow, script, values, library, out _, Catalog.First(d => d.Id == "script.powershell")));
    }

    [Fact]
    public void FillSwapsResultReferencesForKeysAndNamesAMissingPick()
    {
        var (flow, library) = Sample();
        var set = new StepNode("set", "audio.setDefault") { With = new([KeyValuePair.Create("device", (ParamValue)"$result:pick")]) };
        var values = new RunValues();
        StepValues.Fill(flow, set, values, library, out var missing);
        Assert.Equal("pick", missing);
        values.SetDevice("pick", "device", "technics", LibraryKind.Audio);
        Assert.Equal("technics", ((ScalarParam)StepValues.Fill(flow, set, values, library, out missing).With["device"]).Text);
        Assert.Null(missing);
    }

    [Fact]
    public void SingleLineSettingsGetNoLineBreaksAndLongTextIsCut()
    {
        var (flow, library) = Sample();
        var values = new RunValues();
        values.SetText("bt", "error", "one\r\ntwo\nthree");
        Assert.Equal("one two three", StepValues.FillText(flow, "{bt.error}", values, library));
        Assert.Equal("one\ntwo\nthree", StepValues.FillText(flow, "{bt.error}", values, library, singleLine: false).ReplaceLineEndings("\n"));
        values.SetText("bt", "error", new string('x', 5000));
        Assert.True(values.TryGet("bt", "error", out var cut));
        Assert.Equal(RunValues.MaxValueLength, cut.Text.Length);
        Assert.EndsWith("…", cut.Text);
        var filled = StepValues.FillText(flow, string.Concat(Enumerable.Repeat("{bt.error}", 5)), values, library);
        Assert.Equal(StepValues.MaxFilledLength, filled.Length);
    }

    private sealed class Noon : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 12, 5, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void InputsAndRunValuesFill()
    {
        var (flow, library) = Sample();
        var values = new RunValues(time: new Noon()) { FlowName = "F", Trigger = "Alt+Z" };
        values.SetInput("output", LibraryKind.Audio, "buds");
        Assert.Equal("F Alt+Z 12:05 2026-11-12 Galaxy Buds -",
            StepValues.FillText(flow, "{@flow} {@trigger} {@time} {@date} {$output} {$none|-}", values, library));
    }

    [Fact]
    public void ClearForgetsAStepsValuesAndWantedKeepsOnlyReferencedOnes()
    {
        var (flow, _) = Sample();
        var wanted = StepValues.Wanted(flow, Catalog);
        Assert.Equal([("batt", "percent"), ("bt", "device"), ("x", "y")], wanted.Order());
        var values = new RunValues(wanted);
        values.SetDevice("bt", "device", "sony");
        values.SetError("bt", "nope");
        Assert.False(values.TryGet("bt", "error", out _));
        Assert.False(values.Wants("bt", "error"));
        values.Clear("bt");
        Assert.False(values.TryGet("bt", "device", out _));
    }

    [Fact]
    public void WantedIncludesResultReferences()
    {
        var flow = new Flow("f", "F", [new StepNode("set", "audio.setDefault") { With = new([KeyValuePair.Create("device", (ParamValue)"$result:pick")]) }]);
        Assert.Equal([("pick", "device")], StepValues.Wanted(flow, Catalog));
    }

    [Fact]
    public void DescribeShowsTitles()
    {
        var (flow, _) = Sample();
        Assert.Equal("‹Device› ‹Battery (%)›% {x.y} {bt.device} ‹Time› ‹Output›",
            StepValues.Describe(flow, "{bt.device} {batt.percent|?}% {x.y} {{bt.device} {@time} {$output}", Catalog));
    }

    // ---- Existing YAML is untouched (DESIGN §7) ----

    public static TheoryData<string> Files() =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Samples"), "*.yaml").Select(f => Path.GetFileName(f)),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.yaml").Select(f => "Fixtures/" + Path.GetFileName(f)),
    ];

    [Theory]
    [MemberData(nameof(Files))]
    public void SamplesAndFixturesRoundTripByteIdenticalAndGainNoValueProblems(string name)
    {
        var path = name.StartsWith("Fixtures/", StringComparison.Ordinal)
            ? Path.Combine(AppContext.BaseDirectory, name)
            : Path.Combine(AppContext.BaseDirectory, "Samples", name);
        var load = ConfigYaml.Load(File.ReadAllText(path));
        Assert.NotNull(load.Config);
        var saved = ConfigYaml.Save(load.Config);
        var again = ConfigYaml.Load(saved);
        Assert.Equal(load.Config, again.Config);
        Assert.Equal(saved, ConfigYaml.Save(again.Config!));
        var config = load.Config;
        foreach (var flow in config.Flows)
        {
            foreach (var step in flow.Nodes.OfType<StepNode>())
            {
                var descriptor = ProviderCatalog.Find(step.Step);
                if (!step.With.Values.Any(v => PickResults.StepIn(v) is not null || v is MapParam))
                    Assert.Same(step, StepValues.Fill(flow, step, new RunValues(), config.Library, out _));
                foreach (var (setting, value) in step.With)
                {
                    if (value is ScalarParam { Text: var text } && descriptor?.FindParam(setting) is { } spec)
                        Assert.Empty(StepValues.Problems(flow, step.Id, spec, text, defaults: config.Settings.StepDefaults));
                }
            }
        }
    }
}
