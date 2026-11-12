using System.Net;
using System.Text;
using DazeControl.Core.Model;
using DazeControl.Providers.Basic;
using DazeControl.Providers.Files;
using DazeControl.Providers.Http;
using DazeControl.Providers.Process;
using DazeControl.Providers.Pc;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

/// <summary>More step values (D-129, T23.5): what programs print, what a web request answered, what checks read; real processes, fake network.</summary>
public sealed class StepValueFieldsTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A device name that is code in every place a name could be pasted.</summary>
    private const string Evil = "TV'; Remove-Item C:\\x; $(calc) \"q\" `n %TEMP% & é";

    private static StepContext Wanting(StepContext context, bool wanted) => context with { WantsValue = _ => wanted };

    [Fact]
    public async Task AProgramsOutputIsReadOnlyWhenALaterStepUsesIt()
    {
        var used = Wanting(Steps.Context("process.run", ("path", Cmd)).WithList("args", "/c", "echo hello"), wanted: true);
        var unused = Wanting(Steps.Context("process.run", ("path", Cmd)).WithList("args", "/c", "echo hello"), wanted: false);
        await new ProcessRunProvider().ExecuteAsync(used, Token);
        await new ProcessRunProvider().ExecuteAsync(unused, Token);

        Assert.Equal("hello", used.Values["output"].Text.Trim());
        Assert.DoesNotContain("output", unused.Values.Keys);
    }

    [Fact]
    public async Task AFailedProgramStillGivesItsOutputAndExitCode()
    {
        var context = Steps.Context("process.run", ("path", Cmd)).WithList("args", "/c", "echo partial & exit 3");
        await Assert.ThrowsAsync<StepFailedException>(() => new ProcessRunProvider().ExecuteAsync(context, Token));

        Assert.Equal("3", context.Values["exitCode"].Text);
        Assert.Equal("partial", context.Values["output"].Text.Trim()); // the runner keeps it only if the step type's value is OnFailure
    }

    [Fact]
    public async Task OnlyTheTailOfALongOutputIsKept()
    {
        var context = Steps.Context("script.powershell", ("script", "Write-Output ('a' * 5000); Write-Output 'end'"));
        await new PowerShellProvider().ExecuteAsync(context, Token);

        var output = context.Values["output"].Text;
        Assert.True(output.Length <= 1100);
        Assert.EndsWith("end", output.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScriptReadsADeviceNameFromItsEnvironmentAsPlainData()
    {
        // The script text never holds the name (so quotes, ;, $(...) and %VAR% can't change what runs); it reads it from the environment.
        const string script = "[Console]::Out.Write($env:DAZE_BT_DEVICE)";
        var context = Steps.Context("script.powershell", ("script", script)) with
        {
            Variables = new Dictionary<string, string> { ["DAZE_BT_DEVICE"] = Evil },
        };
        await new PowerShellProvider().ExecuteAsync(context, Token);

        Assert.Equal(Evil, context.Values["output"].Text);
        Assert.False(File.Exists("C:\\x"));
    }

    [Fact]
    public async Task AProgramArgumentWithPercentSignsIsNotExpanded()
    {
        var script = Path.Combine(Path.GetTempPath(), $"daze-arg-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(script, "param($a) [Console]::OutputEncoding = [Text.Encoding]::UTF8; [Console]::Out.Write($a)", Token);
        try
        {
            var guarded = Evil.Replace('%', DazeControl.Core.Model.StepValues.PercentGuard);
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var context = Steps.Context("process.run", ("path", powershell))
                .WithList("args", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, guarded);
            await new ProcessRunProvider().ExecuteAsync(context, Token);

            Assert.Equal(Evil, context.Values["output"].Text.Trim()); // %TEMP% is still the text "%TEMP%", not this PC's temp folder
        }
        finally
        {
            File.Delete(script);
        }
    }

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public Credential? Read(string name) => null;
    }

    [Fact]
    public async Task AWebRequestGivesItsStatusAndTheStartOfTheAnswer()
    {
        using var handler = new Answer(HttpStatusCode.OK, new string('x', 3000));
        using var provider = new HttpRequestProvider(handler, new NoCredentials());
        var context = Steps.Context("http.request", ("url", "https://h.example/"));
        await provider.ExecuteAsync(context, Token);

        Assert.Equal("200", context.Values["status"].Text);
        Assert.Equal(1001, context.Values["body"].Text.Length); // one over the limit so the runner's cut shows
    }

    [Fact]
    public async Task AFailedWebRequestStillGivesItsStatusButNotTheBody()
    {
        using var handler = new Answer(HttpStatusCode.NotFound, "no such thing");
        using var provider = new HttpRequestProvider(handler, new NoCredentials());
        var context = Steps.Context("http.request", ("url", "https://h.example/"));
        await Assert.ThrowsAsync<StepFailedException>(() => provider.ExecuteAsync(context, Token));

        Assert.Equal("404", context.Values["status"].Text);
        Assert.DoesNotContain("body", context.Values.Keys);
    }

    [Fact]
    public async Task TheBodyIsNotReadWhenNothingUsesIt()
    {
        using var handler = new Answer(HttpStatusCode.OK, "secret");
        using var provider = new HttpRequestProvider(handler, new NoCredentials());
        var context = Wanting(Steps.Context("http.request", ("url", "https://h.example/")), wanted: false);
        await provider.ExecuteAsync(context, Token);

        Assert.DoesNotContain("body", context.Values.Keys);
        Assert.Equal("200", context.Values["status"].Text);
    }

    [Fact]
    public async Task ACheckGivesItsSentenceEvenWhenItFails()
    {
        var context = Steps.Context("file.check", ("path", Path.Combine(Path.GetTempPath(), "daze-not-there-" + Guid.NewGuid().ToString("N"))), ("is", "exists"));
        await Assert.ThrowsAsync<StepFailedException>(() => new FactCheckProvider("file.check", new FileExistsProbe(), "exists").ExecuteAsync(context, Token));

        Assert.EndsWith("doesn't exist", context.Values["result"].Text, StringComparison.Ordinal);
    }

    private sealed class FakePc(TimeSpan idle = default, int screens = 1, long free = 0) : IPcState
    {
        public PowerReading Power() => default;

        public TimeSpan IdleFor() => idle;

        public bool? Locked() => false;

        public int ScreenCount() => screens;

        public long FreeBytes(string path) => free;
    }

    [Fact]
    public async Task IdleFreeAndCountAreValuesOfTheirChecks()
    {
        var idle = Steps.Context("pc.idle", ("for", "5m"));
        await new IdleProbe(new FakePc(idle: TimeSpan.FromSeconds(3725))).ReadAsync(idle, Token);
        var count = Steps.Context("display.count", ("count", "2"));
        await new ScreenCountProbe(new FakePc(screens: 3)).ReadAsync(count, Token);
        var space = Steps.Context("file.space", ("drive", "C:"), ("gb", "10"));
        await new FreeSpaceProbe(new FakePc(free: 12_345_000_000)).ReadAsync(space, Token);

        Assert.Equal("1h2m5s", idle.Values["idle"].Text);
        Assert.Equal("3", count.Values["count"].Text);
        Assert.Equal("12.3", space.Values["free"].Text);
    }
}
