using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;

namespace DazeControl.Core.Tests;

/// <summary>More step values and the fields that take them (D-129, T23.5), against the real catalog.</summary>
public class StepValueFieldsTests
{
    private static Configuration Load(params string[] nodes)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    name: F\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n")));
        Assert.Empty(load.Problems);
        return load.Config!;
    }

    private const string Producer = "{ id: p, step: process.run, with: { path: x.exe } }";

    private static string Consumer(string with, string step = "process.run") => $"{{ id: c, step: {step}, after: [p], with: {with} }}";

    [Theory]
    [InlineData("process.run", "output", ValueKind.Text, true)]
    [InlineData("script.powershell", "output", ValueKind.Text, true)]
    [InlineData("http.request", "status", ValueKind.Number, false)]
    [InlineData("http.request", "body", ValueKind.Text, true)]
    [InlineData("menu.show", "picked", ValueKind.Text, false)]
    [InlineData("pc.idle", "idle", ValueKind.Text, false)]
    [InlineData("file.space", "free", ValueKind.Number, false)]
    [InlineData("display.count", "count", ValueKind.Number, false)]
    public void TheCatalogDeclaresTheValue(string step, string value, ValueKind kind, bool sensitive)
    {
        var spec = StepValues.Find(ProviderCatalog.Find(step)!, value);

        Assert.NotNull(spec);
        Assert.Equal((kind, sensitive), (spec.Kind, spec.Sensitive));
    }

    [Theory]
    [InlineData("audio.check")]
    [InlineData("bluetooth.check")]
    [InlineData("process.check")]
    [InlineData("service.check")]
    [InlineData("file.check")]
    [InlineData("file.space")]
    [InlineData("network.wifi")]
    [InlineData("network.internet")]
    [InlineData("power.battery")]
    [InlineData("pc.idle")]
    [InlineData("display.count")]
    [InlineData("time.check")]
    [InlineData("android.check")]
    [InlineData("display.check")]
    [InlineData("flag.check")]
    public void EveryFactCheckGivesItsResultEvenWhenItFails(string step)
    {
        var result = StepValues.Find(ProviderCatalog.Find(step)!, "result");

        Assert.NotNull(result);
        Assert.True(result.OnFailure);
        Assert.Single(ProviderCatalog.Find(step)!.Values, v => v.Name == "result");
    }

    [Fact]
    public void OnlyTextThatIsMeantToHoldValuesFillsThem()
    {
        var filling = ProviderCatalog.All.SelectMany(d => d.Params.Where(p => p.TakesValues).Select(p => (d.Id, p.Name, p.Fill))).ToList();

        Assert.Contains(("http.request", "url", FillRule.Url), filling);
        Assert.Contains(("android.open", "address", FillRule.Url), filling);
        Assert.Contains(("process.run", "args", FillRule.Argument), filling);
        // Headers, body, the script, paths and credentials never take values: a value there could change what runs or is sent.
        Assert.DoesNotContain(filling, f => f.Name is "headers" or "body" or "script" or "path" or "credential" or "cwd");
    }

    [Fact]
    public void AValueInAProgramArgumentIsCheckedLikeAnyOther()
    {
        var good = Load(Producer, Consumer("{ path: y.exe, args: [--name, \"{p.output}\"] }"));
        var unknown = Load(Producer, Consumer("{ path: y.exe, args: [\"{p.nope}\"] }"));
        var first = Load("{ id: c, step: process.run, with: { path: y.exe, args: [\"{p.output}\"] } }", "{ id: p, step: process.run, after: [c], with: { path: x.exe } }");

        Assert.DoesNotContain(ConfigValidator.Validate(good), p => p.Severity == Severity.Error);
        Assert.Contains(ConfigValidator.Validate(unknown), p => p.Severity == Severity.Error && p.Message.Contains("gives no value called \"nope\"", StringComparison.Ordinal));
        Assert.Contains(ConfigValidator.Validate(first), p => p.Severity == Severity.Error && p.Message.Contains("must run before", StringComparison.Ordinal));
    }

    [Fact]
    public void ABodyHeaderOrScriptStillTakesTheTextAsWrittenWithAWarning()
    {
        var config = Load(Producer, Consumer("{ url: \"https://h.example/\", body: \"{p.output}\" }", "http.request"));

        Assert.Contains(ConfigValidator.Validate(config), p => p.Severity == Severity.Warning && p.Message.Contains("doesn't take values", StringComparison.Ordinal));
    }

    [Fact]
    public void WantedCountsArgumentsAndAScriptsEnvironmentNames()
    {
        var flow = Load(Producer, Consumer("{ path: y.exe, args: [\"{p.output}\"] }"),
            "{ id: s, step: script.powershell, after: [c], with: { script: \"Write-Host $env:daze_p_exitcode\" } }",
            "{ id: t, step: script.powershell, after: [c], with: { script: \"exit 0\" } }").Flows[0];

        var wanted = StepValues.Wanted(flow);

        Assert.Contains(("p", "output"), wanted); // an argument references it
        Assert.Contains(("p", "exitCode"), wanted); // the script names its variable, in any case
        Assert.DoesNotContain(("p", "error"), wanted);
    }

    [Fact]
    public void AScriptWithNoVariableNamesWantsNothingForItself()
    {
        var flow = Load(Producer, "{ id: s, step: script.powershell, after: [p], with: { script: \"exit 0\" } }").Flows[0];

        Assert.Empty(StepValues.Wanted(flow));
    }

    [Theory]
    [InlineData("bt", "device", "DAZE_BT_DEVICE")]
    [InlineData("bt-1", "exitCode", "DAZE_BT_1_EXITCODE")]
    [InlineData("Wifi", "network", "DAZE_WIFI_NETWORK")]
    public void AVariableNameIsTheStepAndValueInUpperCase(string step, string value, string expected) =>
        Assert.Equal(expected, StepValues.Variable(step, value));

    [Fact]
    public void ScriptVariablesHoldTheEarlierValuesTheScriptNamesAndNothingElse()
    {
        var flow = Load(Producer, "{ id: s, step: script.powershell, after: [p], with: { script: \"$env:DAZE_P_OUTPUT; $env:daze_p_exitcode; $env:DAZE_LATER_OUTPUT\" } }",
            "{ id: later, step: process.run, after: [s], with: { path: z.exe } }").Flows[0];
        var values = new RunValues();
        values.SetText("p", "output", "TV'; calc");
        values.SetNumber("p", "exitCode", 0);
        values.SetText("p", "error", "kept for another step, not named here");
        values.SetText("later", "output", "not yet");

        var variables = StepValues.Variables(flow, "s", values, Library.Empty);

        Assert.Equal(new Dictionary<string, string> { ["DAZE_P_OUTPUT"] = "TV'; calc", ["DAZE_P_EXITCODE"] = "0" }, variables);
    }

    private static Flow FlowWith(string text) =>
        Load(Producer, "{ id: web, step: http.request, after: [p], with: { url: \"" + text + "\" } }").Flows[0];

    [Fact]
    public void AnAddressTakesAValuePercentEncodedAndAFallbackAsWritten()
    {
        var flow = FlowWith("https://h.example/{p.output}/{p.exitCode|a b}");
        var values = new RunValues();
        values.SetText("p", "output", "TV & Co/x?y=1 é");

        var filled = StepValues.FillText(flow, "https://h.example/{p.output}/{p.exitCode|a b}", values, Library.Empty, rule: FillRule.Url);

        Assert.Equal("https://h.example/TV%20%26%20Co%2Fx%3Fy%3D1%20%C3%A9/a b", filled);
    }

    [Fact]
    public void AnArgumentGuardsPercentSignsAndUnguardPutsThemBack()
    {
        var flow = FlowWith("x");
        var values = new RunValues();
        values.SetText("p", "output", "50% %PATH%");

        var filled = StepValues.FillText(flow, "--a=%TEMP% {p.output}", values, Library.Empty, singleLine: false, rule: FillRule.Argument);

        Assert.Equal("--a=%TEMP% 50" + StepValues.PercentGuard + " " + StepValues.PercentGuard + "PATH" + StepValues.PercentGuard, filled);
        Assert.Equal("--a=%TEMP% 50% %PATH%", StepValues.Unguard(filled));
    }

    [Fact]
    public void SecretsAreTheSensitiveValuesOfEarlierStepsAndMaskHidesThem()
    {
        var flow = Load(Producer, "{ id: web, step: http.request, after: [p], with: { url: \"https://h.example/\" } }").Flows[0];
        var values = new RunValues();
        values.SetText("p", "output", "tok-123456");
        values.SetNumber("p", "exitCode", 7);

        var secrets = StepValues.Secrets(flow, "web", values, Library.Empty);

        Assert.Equal([("tok-123456", "Output")], secrets);
        Assert.Equal("failed with ‹Output›: ‹Output›", StepValues.Mask("failed with tok-123456: tok-123456", secrets));
        Assert.Equal("exit code 7", StepValues.Mask("exit code 7", secrets)); // not Sensitive
        Assert.Null(StepValues.Mask(null, secrets));
        Assert.Empty(StepValues.Secrets(flow, "p", values, Library.Empty)); // a step's own values aren't "earlier"
    }
}
