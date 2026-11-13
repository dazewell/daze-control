using System.Net;
using System.Net.Sockets;
using DazeControl.Providers.Network;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

/// <summary>T24.8 (D-140): Check address behind a fake probe; the real probes only against loopback and a fake HTTP handler.</summary>
public class NetworkReachTests
{
    private sealed class FakeProbe(ProbeResult result) : IReachProbe
    {
        public List<string> Calls { get; } = [];

        public Task<ProbeResult> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add($"tcp {host} {port} {timeout.TotalSeconds}");
            return Task.FromResult(result);
        }

        public Task<ProbeResult> WebAsync(Uri uri, bool headOnly, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add($"web {uri} {(headOnly ? "HEAD" : "GET")} {timeout.TotalSeconds}");
            return Task.FromResult(result);
        }

        public Task<ProbeResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add($"ping {host} {timeout.TotalSeconds}");
            return Task.FromResult(result);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (answer(request) is { } response)
                return response;
            await Task.Delay(Timeout.Infinite, cancellationToken); // never answers: only the probe's wait ends it
            throw new InvalidOperationException();
        }
    }

    private static readonly ProbeResult Up = new(true, 12);

    private static async Task<(StepContext Context, StepFailedException? Failure)> Run(IReachProbe probe, params (string, string)[] with)
    {
        var context = Steps.Context("network.reach", with);
        try
        {
            await new NetworkReachProvider(probe).ExecuteAsync(context, TestContext.Current.CancellationToken);
            return (context, null);
        }
        catch (StepFailedException e)
        {
            return (context, e);
        }
    }

    [Theory]
    [InlineData("nas:445", "tcp nas 445 5")]
    [InlineData("  nas.local:445  ", "tcp nas.local 445 5")]
    [InlineData("192.168.1.10:22", "tcp 192.168.1.10 22 5")]
    [InlineData("[::1]:80", "tcp ::1 80 5")]
    [InlineData("nas", "ping nas 5")]
    [InlineData("192.168.1.1", "ping 192.168.1.1 5")]
    [InlineData("::1", "ping ::1 5")]
    [InlineData("https://example.com/a?b=1", "web https://example.com/a?b=1 GET 5")]
    public async Task TheFormOfTheAddressPicksTheProbe(string address, string call)
    {
        var probe = new FakeProbe(Up);
        var (context, failure) = await Run(probe, ("address", address));
        Assert.Null(failure);
        Assert.Equal(call, Assert.Single(probe.Calls));
        Assert.Equal("12", context.Values["ms"].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nas:0")]
    [InlineData("nas:65536")]
    [InlineData("nas:+80")]
    [InlineData("nas:80x")]
    [InlineData("nas:")]
    [InlineData("a b:80")]
    [InlineData("nas:80\n; calc")]
    [InlineData("ftp://example.com")]
    [InlineData("http://")]
    [InlineData("http://user:secret@example.com")]
    [InlineData("1.2.3:80")]
    [InlineData("[::1")]
    [InlineData("[nas]:80")]
    [InlineData("fe80::1%4")]
    [InlineData("-bad.example:80")]
    [InlineData("a..b:80")]
    public async Task AnAddressThatIsNotOneFailsWithASentenceAndSendsNothing(string address)
    {
        var probe = new FakeProbe(Up);
        var (context, failure) = await Run(probe, ("address", address));
        Assert.NotNull(failure);
        Assert.Empty(probe.Calls);
        Assert.Equal(failure.Message, context.Values["result"].Text);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACredentialInAWebAddressIsRefusedAndNeverRepeated()
    {
        var probe = new FakeProbe(new ProbeResult(true, 5, 200));
        var (_, failure) = await Run(probe, ("address", "https://me:hunter2@example.com/x?token=abc"));
        Assert.Empty(probe.Calls);
        Assert.DoesNotContain("hunter2", failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("me:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWebSentenceNamesTheHostOnlyNeverThePathOrQuery()
    {
        var (context, failure) = await Run(new FakeProbe(new ProbeResult(true, 30, 200)), ("address", "https://example.com/private/path?token=abc"));
        Assert.Null(failure);
        Assert.Equal("example.com answers 200 in 30 ms", context.Values["result"].Text);
        Assert.Equal("200", context.Values["status"].Text);
    }

    [Theory]
    [InlineData("", 503, true)]            // any answer counts
    [InlineData("200-299", 204, true)]
    [InlineData("200-299", 503, false)]
    [InlineData("200-299, 401", 401, true)]
    [InlineData("200", 201, false)]
    public async Task TheExpectedStatusRangeDecides(string statuses, int answered, bool reachable)
    {
        var (_, failure) = await Run(new FakeProbe(new ProbeResult(true, 5, answered)), ("address", "https://example.com"), ("status", statuses));
        Assert.Equal(reachable, failure is null);
    }

    [Fact]
    public async Task NotReachableFailsWhenItAnswersAndPassesWhenItDoesNot()
    {
        var down = new ProbeResult(false, 5000, Reason: "didn't answer in time");
        var (context, failure) = await Run(new FakeProbe(down), ("address", "nas:445"));
        Assert.Equal("nas:445 didn't answer in time", failure!.Message);
        Assert.False(context.Values.ContainsKey("ms"));

        var (_, passes) = await Run(new FakeProbe(down), ("address", "nas:445"), ("is", "notReachable"));
        Assert.Null(passes);

        var (_, fails) = await Run(new FakeProbe(Up), ("address", "nas:445"), ("is", "notReachable"));
        Assert.Equal("nas:445 is reachable.", fails!.Message);
    }

    [Fact]
    public async Task HeadAndTheTimeoutAreHandedToTheProbe()
    {
        var probe = new FakeProbe(new ProbeResult(true, 1, 200));
        await Run(probe, ("address", "http://example.com"), ("method", "HEAD"), ("timeout", "2s"));
        Assert.Equal("web http://example.com/ HEAD 2", Assert.Single(probe.Calls));
    }

    [Theory]
    [InlineData("500ms")]
    [InlineData("21s")]
    [InlineData("soon")]
    public async Task ATimeoutOutsideOneToTwentySecondsFails(string timeout)
    {
        var probe = new FakeProbe(Up);
        var (_, failure) = await Run(probe, ("address", "nas:445"), ("timeout", timeout));
        Assert.NotNull(failure);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task ACancelledRunStopsBeforeProbing()
    {
        var probe = new FakeProbe(Up);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NetworkReachProvider(probe).ExecuteAsync(Steps.Context("network.reach", ("address", "nas:445")), cancelled.Token));
        Assert.Empty(probe.Calls);
    }

    // The real probes, against loopback and a fake handler only.

    [Fact]
    public async Task ARealTcpConnectToALoopbackListenerAnswers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await new WindowsReachProbe().TcpAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Answered);
    }

    [Fact]
    public async Task ARealTcpConnectToAClosedLoopbackPortDoesNotAnswer()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var result = await new WindowsReachProbe().TcpAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.Answered);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task TheCallersCancellationIsNotATimeout()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsReachProbe(() => new FakeHandler(_ => null)).WebAsync(new Uri("http://example.com"), false, TimeSpan.FromSeconds(5), cancelled.Token));
    }

    [Fact]
    public async Task ARealWebProbeReportsTheFirstAnswerWithoutSendingCredentials()
    {
        HttpRequestMessage? seen = null;
        var probe = new WindowsReachProbe(() => new FakeHandler(r =>
        {
            seen = r;
            return new HttpResponseMessage(HttpStatusCode.Redirect);
        }));
        var result = await probe.WebAsync(new Uri("https://example.com/a"), true, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Answered);
        Assert.Equal(302, result.Status);
        Assert.Equal(HttpMethod.Head, seen!.Method);
        Assert.Null(seen.Headers.Authorization);
    }

    [Fact]
    public async Task AWebProbeThatGetsNoAnswerTimesOutWithASentence()
    {
        var probe = new WindowsReachProbe(() => new FakeHandler(_ => null));
        var result = await probe.WebAsync(new Uri("https://example.com"), false, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(result.Answered);
        Assert.Equal("didn't answer in time", result.Reason);
    }
}
