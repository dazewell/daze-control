using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Core.Yaml;
using CtrlAltDaze.Ipc;
using CtrlAltDaze.Runtime.Control;
using CtrlAltDaze.Runtime.Lifecycle;
using CtrlAltDaze.Runtime.Providers;
using static CtrlAltDaze.Runtime.Tests.PromptRunTests;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>T24.9: inputs given from the command line (<c>cad run flow name=value</c>, <c>--run flow name=value</c>).</summary>
public sealed class RunInputsTests : IDisposable
{
    private const string Yaml = """
        library:
          audio:
            technics: { match: Technics }
            sony: { match: Sony }
          bluetooth:
            sony: { address: "AC:80:0A:11:22:33" }
            phone: { address: "AC:80:0A:11:22:44" }
        flows:
          connect:
            name: Connect
            inputs:
              out: { kind: audio, name: Output, default: technics }
              greeting: { kind: text, name: Greeting, default: Hi }
            nodes:
              - { id: set, step: audio.setDefault, with: { device: $out } }
              - { id: say, step: notify, with: { text: "{$greeting}" }, after: [set] }
          plain:
            name: Plain
            nodes: [{ id: a, step: notify, with: { text: x } }]
        """;

    private readonly ManualTime _time = new();
    private readonly List<string> _seen = [];
    private readonly RunHistory _history = new(null);
    private readonly List<string> _log = [];
    private readonly EngineController _controller;
    private readonly string _root = @"Software\CtrlAltDaze.Tests\" + Guid.NewGuid().ToString("N");

    public RunInputsTests()
    {
        var snapshot = ConfigHost.Apply(ConfigSnapshot.Empty, ConfigYaml.Load(Yaml));
        FlowRunner Runner() => new(
        [
            new Recorder("audio.setDefault", c => { lock (_seen) _seen.Add("device=" + ((ScalarParam)c.Node.With["device"]).Text); }),
            new Recorder("notify", c => { lock (_seen) _seen.Add("text=" + ((ScalarParam)c.Node.With["text"]).Text); }),
        ], _time);
        _controller = new EngineController(() => snapshot, new FlowLauncher(Runner(), _history), _history, EngineController.CreateDryRunner(_time),
            new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, @"C:\x\CtrlAltDaze.exe", "c.yaml"),
            line => { lock (_log) _log.Add(line); });
    }

    public void Dispose()
    {
        _time.Dispose();
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    private Task<PipeMessage> Run(string flow, Dictionary<string, string>? inputs, bool dry = false, bool exactId = false) =>
        _controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.Run, Flow = flow, Wait = true, DryRun = dry, ExactId = exactId, Inputs = inputs },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task ADeviceAndATextInputReachTheRun()
    {
        var answer = await Run("connect", new() { ["out"] = "sony", ["greeting"] = "Hello {bt.device} $out" });

        Assert.True(answer.Ok, answer.Error);
        Assert.Equal(FlowOutcome.Succeeded, answer.Run!.Outcome);
        // The text is data: a reference-looking value is not read, and the device is the Library key given.
        Assert.Equal(["device=sony", "text=Hello {bt.device} $out"], _seen);
    }

    [Fact]
    public async Task WithoutInputsTheDefaultsRun()
    {
        Assert.True((await Run("connect", null)).Ok);

        Assert.Equal(["device=technics", "text=Hi"], _seen);
    }

    [Theory]
    [InlineData("nope", "has no input called \"nope\". Its inputs: out, greeting.")]
    [InlineData("out", "called \"x\" in your Library")]
    public async Task ABadInputIsAUsageErrorAndNothingRuns(string name, string message)
    {
        var answer = await Run("connect", new() { [name] = "x" });

        Assert.False(answer.Ok);
        Assert.True(answer.Usage);
        Assert.Contains(message, answer.Error, StringComparison.Ordinal);
        Assert.Empty(_seen);
        Assert.Empty(_history.Recent);
    }

    [Fact]
    public async Task AKeyOfAnotherKindIsRefused()
    {
        // "phone" is a Bluetooth device; the Output input takes an audio one.
        var answer = await Run("connect", new() { ["out"] = "phone" });

        Assert.True(answer.Usage);
        Assert.Contains("called \"phone\" in your Library", answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlowWithoutInputsSaysSo()
    {
        var answer = await Run("plain", new() { ["x"] = "1" });

        Assert.True(answer.Usage);
        Assert.Contains("Plain has no inputs", answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATextTooLongIsRefusedWithoutShowingIt()
    {
        var value = new string('s', FlowInputs.MaxTextLength + 1);

        var answer = await Run("connect", new() { ["greeting"] = value });

        Assert.True(answer.Usage);
        Assert.DoesNotContain(value, answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretTextIsNeverLogged()
    {
        await Run("connect", new() { ["greeting"] = "s3cret-token" });
        await Run("connect", new() { ["nope"] = "s3cret-token" });

        lock (_log)
            Assert.DoesNotContain(_log, l => l.Contains("s3cret-token", StringComparison.Ordinal));
        Assert.DoesNotContain(_history.Recent, r => r.Trigger.Contains("s3cret-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingStepNeverShowsAGivenTextInTheRecordOrHistory()
    {
        const string secret = "s3cret-token";
        var load = ConfigYaml.Load("""
            flows:
              boom:
                name: Boom
                inputs:
                  token: { kind: text, name: Token, default: none }
                nodes:
                  - { id: a, step: notify, with: { text: "{$token}" } }
            """);
        var snapshot = ConfigHost.Apply(ConfigSnapshot.Empty, load);
        var runner = new FlowRunner([new Recorder("notify", c => throw new StepFailedException("Rejected " + secret))], _time);
        var history = new RunHistory(null);
        var controller = new EngineController(() => snapshot, new FlowLauncher(runner, history), history, EngineController.CreateDryRunner(_time),
            new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, @"C:\x\CtrlAltDaze.exe", "c.yaml"), _ => { });

        var answer = await controller.HandleAsync(new PipeRequest
        {
            Id = 7, Op = Ops.Run, Flow = "boom", Wait = true, Inputs = new() { ["token"] = secret },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(FlowOutcome.Failed, answer.Run!.Outcome);
        Assert.NotNull(answer.Run.Nodes[0].Error);
        Assert.DoesNotContain(secret, answer.Run.Nodes[0].Error, StringComparison.Ordinal);
        Assert.DoesNotContain(history.Recent, r => r.Nodes.Any(n => n.Error?.Contains(secret, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task ADesktopRunWithABadInputTellsThePopup()
    {
        var refused = new List<RunRefusal>();
        _controller.RunRefused += refused.Add;

        await Run("connect", new() { ["nope"] = "x" }, exactId: true);

        Assert.Contains("no input called \"nope\"", Assert.Single(refused).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunChecksInputsToo()
    {
        Assert.True((await Run("connect", new() { ["out"] = "sony" }, dry: true)).Ok);
        Assert.True((await Run("connect", new() { ["out"] = "zzz" }, dry: true)).Usage);
    }

    [Fact]
    public void RunArgumentsTakeNameValueWordsAfterTheFlow()
    {
        var parsed = EngineArguments.Parse(["--run", "connect", "out=sony", "greeting=a=b c", @"C:\cfg\my=cfg.yaml"]);

        Assert.Equal("connect", parsed.RunFlowId);
        Assert.Equal(@"C:\cfg\my=cfg.yaml", parsed.Config);
        Assert.Equal("sony", parsed.RunInputs!["out"]);
        Assert.Equal("a=b c", parsed.RunInputs["greeting"]);
        Assert.Null(EngineArguments.Parse(["--run", "connect"]).RunInputs);
        // A config of the shape name=value goes before --run (Format does that) and is never taken for an input.
        var shortcut = EngineArguments.Parse(EngineArguments.Split(EngineArguments.Format("connect", "settings=prod.yaml")));
        Assert.Equal("settings=prod.yaml", shortcut.Config);
        Assert.Equal("connect", shortcut.RunFlowId);
        Assert.Null(shortcut.RunInputs);
        Assert.Equal("settings=prod.yaml", EngineArguments.Parse(["settings=prod.yaml", "--run", "connect", "out=sony"]).Config);
        Assert.Equal(@"C:\c.yaml", EngineArguments.Parse(["--run", "connect", @"C:\c.yaml"]).Config); // older shortcuts still work
        Assert.Equal("a=b", EngineArguments.Parse(["a=b"]).Config); // no --run: still a config path
    }

    [Fact]
    public void CommandLineTakesNameValueWords()
    {
        var line = CommandLine.Parse("run", ["connect", "out=sony", "--wait", "greeting=", "t=a=b"]);

        Assert.Equal("connect", line.Word);
        Assert.Equal("sony", line.Inputs!["out"]);
        Assert.Equal("", line.Inputs["greeting"]);
        Assert.Equal("a=b", line.Inputs["t"]);
        Assert.Null(CommandLine.Parse("run", ["connect"]).Inputs);
    }

    [Theory]
    [InlineData("connect", "out")]
    [InlineData("connect", "=x")]
    [InlineData("connect", "out=a", "out=b")]
    public void BadInputWordsAreUsageErrors(params string[] words) =>
        Assert.Throws<CommandLineException>(() => CommandLine.Parse("run", words));
}
