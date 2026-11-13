using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Runtime.Providers;
using static CtrlAltDaze.Runtime.Tests.FlowRunnerTests;
using static CtrlAltDaze.Runtime.Tests.PromptRunTests;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>
/// More step values and the fields that take them (D-129, T23.5): <c>output</c>, address and argument fill rules, Sensitive
/// values kept out of errors and summaries (D-014), environment variables for scripts. Fake providers, no devices.
/// </summary>
public sealed class StepValueFieldsTests : IDisposable
{
    /// <summary>A name that is code in every place a name could be pasted: quotes, a separator, a subexpression, a variable, a space.</summary>
    private const string Evil = "TV'; calc & $(calc) \"x\" %TEMP% é";

    private readonly ManualTime _time = new();

    public void Dispose() => _time.Dispose();

    private static (Flow Flow, Library Library) Load(params string[] nodes)
    {
        var load = ConfigYaml.Load("flows:\n  f:\n    name: Evening\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n")));
        Assert.Empty(load.Problems);
        Assert.DoesNotContain(CtrlAltDaze.Core.Validation.ConfigValidator.Validate(load.Config!), p => p.Severity == CtrlAltDaze.Core.Validation.Severity.Error);
        return (load.Config!.Flows[0], load.Config.Library);
    }

    private static async Task<RunResult> Run(FlowRunner runner, (Flow Flow, Library Library) loaded) =>
        await Bound(runner.RunAsync(loaded.Flow, loaded.Library, [], TestContext.Current.CancellationToken, "Alt+Z"));

    private const string Producer = "{ id: p, step: process.run, with: { path: x.exe } }";

    [Fact]
    public async Task AnAddressTakesAValueAsOnePercentEncodedPart()
    {
        var loaded = Load(Producer, "{ id: web, step: http.request, after: [p], with: { url: \"https://h.example/say?name={p.output}&x=1\" } }");
        string? url = null;
        var runner = new FlowRunner([new Recorder("process.run", c => c.SetValue("output", Evil)),
            new Recorder("http.request", c => url = ((ScalarParam)c.Node.With["url"]).Text)], _time);
        await Run(runner, loaded);

        Assert.Equal("https://h.example/say?name=" + Uri.EscapeDataString(Evil) + "&x=1", url);
        Assert.NotNull(url);
        Assert.DoesNotContain(' ', url);
        Assert.DoesNotContain('\'', url);
        Assert.DoesNotContain("$(", url, StringComparison.Ordinal);
        Assert.Equal(2, url.Split('&').Length); // the value's own & did not start a parameter
    }

    [Fact]
    public async Task AnArgumentTakesAValueAsOneItemWithItsPercentSignsGuarded()
    {
        var loaded = Load(Producer, "{ id: run, step: process.run, after: [p], with: { path: y.exe, args: [--name, \"{p.output}\", \"%TEMP%\\\\{p.output}\"] } }");
        List<string>? args = null;
        var runner = new FlowRunner([new Recorder("process.run", c =>
        {
            if (c.Node.Id == "p")
                c.SetValue("output", Evil);
            else
                args = [.. ((ListParam)c.Node.With["args"]).Items.Select(i => ((ScalarParam)i).Text)];
        })], _time);
        await Run(runner, loaded);

        Assert.NotNull(args);
        Assert.Equal(3, args.Count); // still one argument per item: the value is never split
        Assert.Equal("--name", args[0]);
        Assert.Equal(Evil.Replace('%', StepValues.PercentGuard), args[1]);
        Assert.Equal("%TEMP%\\" + Evil.Replace('%', StepValues.PercentGuard), args[2]); // %TEMP% written by the person is theirs to expand
        Assert.Equal(Evil, StepValues.Unguard(args[1]));
    }

    [Fact]
    public async Task ASensitiveValueNeverReachesAnErrorOrASummary()
    {
        var loaded = Load(Producer, "{ id: web, step: http.request, after: [p], with: { url: \"https://h.example/?k={p.output}\" } }");
        const string secret = "tok-123456";
        var runner = new FlowRunner([new Recorder("process.run", c => c.SetValue("output", secret)),
            new Recorder("http.request", c =>
            {
                c.SetSummary("Sent " + secret);
                throw new StepFailedException($"https://h.example/?k={secret} answered 500");
            })], _time);
        var result = await Run(runner, loaded);

        var web = result.Nodes.Single(n => n.NodeId == "web");
        Assert.DoesNotContain(secret, web.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, web.Summary, StringComparison.Ordinal);
        Assert.Contains("‹Output›", web.Error, StringComparison.Ordinal);
        Assert.Contains("‹Output›", web.Summary, StringComparison.Ordinal);
    }

    private const string Wifi = "{ id: w, step: network.wifi }";

    [Fact]
    public async Task AFinishedStepShowsItsValuesAndTheTextItGot()
    {
        var loaded = Load(Wifi, "{ id: n, step: notify, after: [w], with: { text: \"On {w.network} at {@flow}\" } }");
        var runner = new FlowRunner([new Recorder("network.wifi", c => c.SetValue("network", "Home")), new Recorder("notify", _ => { })], _time);
        var result = await Run(runner, loaded);

        var wifi = Assert.Single(result.Nodes, x => x.NodeId == "w");
        Assert.Equal(new ShownValue("Wi-Fi network", "Home"), Assert.Single(wifi.Values!));
        var notify = Assert.Single(result.Nodes, x => x.NodeId == "n");
        Assert.Equal(new ShownValue("Message", "On Home at Evening"), Assert.Single(notify.Used!));
        Assert.Null(notify.Values); // it gave nothing a later step uses
    }

    [Fact]
    public async Task AValueAndTheTextItGotAreCutAtTwoHundredFiftySixCharacters()
    {
        var loaded = Load(Wifi, "{ id: n, step: notify, after: [w], with: { text: \"{w.network}\" } }");
        var runner = new FlowRunner([new Recorder("network.wifi", c => c.SetValue("network", new string('x', 900))), new Recorder("notify", _ => { })], _time);
        var result = await Run(runner, loaded);

        var shown = Assert.Single(result.Nodes.Single(x => x.NodeId == "w").Values!);
        Assert.Equal(ShownValues.MaxLength, shown.Text.Length);
        Assert.EndsWith("…", shown.Text, StringComparison.Ordinal);
        Assert.Equal(ShownValues.MaxLength, Assert.Single(result.Nodes.Single(x => x.NodeId == "n").Used!).Text.Length);
    }

    [Fact]
    public async Task ASensitiveValueAndTheTextBuiltFromItAreNeverStored()
    {
        const string secret = "tok-123456";
        var loaded = Load(Producer, "{ id: web, step: http.request, after: [p], with: { url: \"https://h.example/?k={p.output}\" } }",
            "{ id: mk, step: text.make, after: [p], with: { text: \"key {p.output}\" } }",
            "{ id: note, step: notify, after: [mk], with: { text: \"Got {mk.text} ({mk.text})\" } }");
        var runner = new FlowRunner([new Recorder("process.run", c => c.SetValue("output", secret)),
            new Recorder("http.request", _ => { }), new Recorder("text.make", c => c.SetValue("text", "key " + secret)), new Recorder("notify", _ => { })], _time);
        var result = await Run(runner, loaded);

        var file = Path.Combine(Path.GetTempPath(), "dc-shown-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            new RunHistory(file).Add(new RunRecord(result.RunId, "f", "Evening", "Alt+Z", result.Started, result.Duration, result.Outcome, [.. result.Nodes]));
            var stored = File.ReadAllText(file);
            Assert.Contains("\"hidden\":true", stored, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(secret), stored, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
        Assert.True(Assert.Single(result.Nodes.Single(x => x.NodeId == "p").Values!).Hidden);
        Assert.True(Assert.Single(result.Nodes.Single(x => x.NodeId == "web").Used!).Hidden);
        Assert.True(Assert.Single(result.Nodes.Single(x => x.NodeId == "mk").Values!).Hidden);
        Assert.True(Assert.Single(result.Nodes.Single(x => x.NodeId == "note").Used!).Hidden); // runtime-sensitive Make text
    }

    private static (ShownValue[]? Values, ShownValue[]? Used) Shown(Flow flow, string stepId, RunValues values, Library library, StepNode filled, Configuration? config = null)
    {
        Func<string, Flow?>? flows = config is null ? null : id => config.Flows.FirstOrDefault(f => f.Id == id);
        var step = (StepNode)flow.FindNode(stepId)!;
        var secrets = StepValues.Secrets(flow, stepId, values, library, flows: flows);
        var descriptor = CtrlAltDaze.Core.Catalog.ProviderCatalog.Find(step.Step)!;
        return ShownValues.Of(flow, step, filled, descriptor, values, library, secrets, CtrlAltDaze.Core.Catalog.ProviderCatalog.All, flows);
    }

    [Fact]
    public void ATextBuiltFromASensitiveInputIsHiddenWholeNotMasked()
    {
        var load = ConfigYaml.Load("""
            flows:
              f:
                name: Evening
                inputs:
                  k: { kind: text, name: Key }
                nodes:
                  - { id: mk, step: text.make, with: { text: "key {$k}" } }
                  - { id: n, step: notify, after: [mk], with: { text: "Got {mk.text}" } }
            """);
        var flow = load.Config!.Flows[0];
        var values = new RunValues();
        values.SetInputText("k", "s3cret", sensitive: true);
        values.SetText("mk", "text", "key s3cret");
        var mk = (StepNode)flow.FindNode("mk")!;

        var made = Shown(flow, "mk", values, load.Config.Library, mk with { With = new([KeyValuePair.Create("text", (ParamValue)"key s3cret")]) });

        Assert.Equal(new ShownValue("Text", "", Hidden: true), Assert.Single(made.Values!));
        Assert.Equal(new ShownValue("Text", "", Hidden: true), Assert.Single(made.Used!));
        // The step after it reads the made text: it is a derived text with the secret in it, hidden too.
        values.MarkSensitive("mk", "text");
        var note = (StepNode)flow.FindNode("n")!;
        var used = Shown(flow, "n", values, load.Config.Library, note with { With = new([KeyValuePair.Create("text", (ParamValue)"Got key s3cret")]) });
        Assert.True(Assert.Single(used.Used!).Hidden);
        Assert.All(made.Values!.Concat(made.Used!).Concat(used.Used!), v => Assert.DoesNotContain("s3cret", v.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void AFlowCallsSensitiveOutputAndTheTextBuiltFromItAreHidden()
    {
        var load = ConfigYaml.Load("""
            flows:
              inner:
                name: Inner
                outputs:
                  said: "x"
                nodes:
                  - { id: a, step: notify, with: { text: hi } }
              f:
                name: Evening
                nodes:
                  - { id: call, step: flow.run, with: { flow: inner } }
                  - { id: n, step: notify, after: [call], with: { text: "Said: {call.said}" } }
            """);
        var flow = load.Config!.Flows.Single(x => x.Id == "f");
        var values = new RunValues();
        values.SetText("call", "said", "s3cret from x");
        values.MarkSensitive("call", "said");
        var call = (StepNode)flow.FindNode("call")!;

        var called = Shown(flow, "call", values, load.Config.Library, call, load.Config);
        var note = (StepNode)flow.FindNode("n")!;
        var used = Shown(flow, "n", values, load.Config.Library, note with { With = new([KeyValuePair.Create("text", (ParamValue)"Said: s3cret from x")]) }, load.Config);

        Assert.True(Assert.Single(called.Values!).Hidden);
        Assert.True(Assert.Single(used.Used!).Hidden);
        Assert.All(called.Values!.Concat(used.Used!), v => Assert.Equal("", v.Text));
    }

    [Fact]
    public void AStepStoresNoMoreThanItsBudget()
    {
        var load = ConfigYaml.Load("""
            flows:
              f:
                name: Evening
                nodes:
                  - { id: w, step: network.wifi }
                  - { id: n, step: notify, after: [w], with: { text: "{w.network}" } }
            """);
        var flow = load.Config!.Flows[0];
        var values = new RunValues();
        values.SetText("w", "network", new string('x', 900));
        var note = (StepNode)flow.FindNode("n")!;
        var shown = Shown(flow, "w", values, load.Config.Library, (StepNode)flow.FindNode("w")!);
        var used = Shown(flow, "n", values, load.Config.Library, note with { With = new([KeyValuePair.Create("text", (ParamValue)new string('y', 900))]) });

        Assert.InRange(shown.Values!.Concat(used.Used!).Sum(v => v.Text.Length), 1, ShownValues.MaxChars);
        Assert.All(shown.Values!.Concat(used.Used!), v => Assert.InRange(v.Text.Length, 0, ShownValues.MaxLength));
    }

    [Fact]
    public async Task ADryRunStoresNoValues()
    {
        var loaded = Load(Wifi, "{ id: n, step: notify, after: [w], with: { text: \"On {w.network}\" } }");
        var runner = new FlowRunner([new Recorder("network.wifi", c => c.SetValue("network", "Home")), new Recorder("notify", _ => { })], _time) { DryRun = true };
        var result = await Run(runner, loaded);

        Assert.All(result.Nodes, x => Assert.Null(x.Values ?? x.Used));
    }

    [Fact]
    public async Task ASensitiveErrorMaskAlsoCoversTheStepsOwnErrorValue()
    {
        const string secret = "tok-123456";
        var loaded = Load(Producer, "{ id: web, step: http.request, after: [p], with: { url: \"https://h.example/?k={p.output}\" } }",
            "{ id: note, step: notify, after: [{ id: web, when: failure }], with: { text: \"Failed: {web.error}\" } }");
        string? said = null;
        var runner = new FlowRunner([new Recorder("process.run", c => c.SetValue("output", secret)),
            new Recorder("http.request", _ => throw new StepFailedException("bad " + secret)),
            new Recorder("notify", c => said = ((ScalarParam)c.Node.With["text"]).Text)], _time);
        await Run(runner, loaded);

        Assert.Equal("Failed: bad ‹Output›", said);
    }

    [Fact]
    public async Task AScriptGetsValuesAsEnvironmentAndItsTextIsNeverChanged()
    {
        const string script = "Write-Output $env:DAZE_P_OUTPUT";
        var loaded = Load(Producer, "{ id: s, step: script.powershell, after: [p], with: { script: \"" + script + "\" } }");
        IReadOnlyDictionary<string, string>? variables = null;
        string? text = null;
        var runner = new FlowRunner([new Recorder("process.run", c => c.SetValue("output", Evil)),
            new Recorder("script.powershell", c => { variables = c.Variables; text = ((ScalarParam)c.Node.With["script"]).Text; })], _time);
        await Run(runner, loaded);

        Assert.Equal(script, text);
        Assert.Equal(Evil, variables!["DAZE_P_OUTPUT"]);
    }

    [Fact]
    public async Task AScriptOnlyCostsTheOutputItNames()
    {
        var loaded = Load(Producer, "{ id: s, step: script.powershell, after: [p], with: { script: \"exit 0\" } }");
        var wanted = new List<bool>();
        var runner = new FlowRunner([new Recorder("process.run", c => wanted.Add(c.Wants("output"))), new Recorder("script.powershell", _ => { })], _time);
        await Run(runner, loaded);

        Assert.Equal([false], wanted);
    }

    [Fact]
    public async Task AStepAfterAHttpRequestSeesItsStatusEvenWhenItFailed()
    {
        var loaded = Load("{ id: web, step: http.request, with: { url: \"https://h.example/\" } }",
            "{ id: note, step: notify, after: [{ id: web, when: failure }], with: { text: \"Got {web.status} and [{web.body|nothing}]\" } }");
        string? said = null;
        var runner = new FlowRunner([new Recorder("http.request", c =>
        {
            c.SetNumber("status", 503);
            c.SetValue("body", "ignored on failure");
            throw new StepFailedException("h.example answered 503.");
        }), new Recorder("notify", c => said = ((ScalarParam)c.Node.With["text"]).Text)], _time);
        await Run(runner, loaded);

        Assert.Equal("Got 503 and [nothing]", said);
    }

    [Fact]
    public async Task AFailedCheckStillGivesItsResultAndNumbers()
    {
        var loaded = Load("{ id: b, step: power.battery, with: { percent: 50 } }",
            "{ id: note, step: notify, after: [{ id: b, when: failure }], with: { text: \"{b.result} / {b.percent}\" } }");
        string? said = null;
        var runner = new FlowRunner([new Recorder("power.battery", c =>
        {
            c.SetValue("result", "The battery is at 20%");
            c.SetNumber("percent", 20);
            throw new StepFailedException("The battery is at 20%");
        }), new Recorder("notify", c => said = ((ScalarParam)c.Node.With["text"]).Text)], _time);
        await Run(runner, loaded);

        Assert.Equal("The battery is at 20% / 20", said);
    }

    [Fact]
    public async Task ADryRunShowsTitlesForAddressesAndArguments()
    {
        var loaded = Load(Producer, "{ id: run, step: process.run, after: [p], with: { path: y.exe, args: [\"{p.output}\"] } }",
            "{ id: web, step: http.request, after: [run], with: { url: \"https://h.example/{p.output}\" } }");
        var seen = new List<string>();
        var runner = new FlowRunner([new Recorder("process.run", c =>
        {
            if (c.Node.Id == "run")
                seen.Add(((ScalarParam)((ListParam)c.Node.With["args"]).Items[0]).Text);
        }), new Recorder("http.request", c => seen.Add(((ScalarParam)c.Node.With["url"]).Text))], _time) { DryRun = true };
        await Run(runner, loaded);

        Assert.Equal(["‹Output›", "https://h.example/‹Output›"], seen);
    }
}
