using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Providers.Http;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Tests;

public sealed class HttpProviderTests : IDisposable
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { ReasonPhrase = "token abc" }; // a server may echo anything here
        }
    }

    private sealed class FakeCredentials : ICredentialStore
    {
        public Credential? Read(string name) => name switch
        {
            "sunshine" => new Credential("admin", "s3cret"),
            "token" => new Credential("", "abc"),
            _ => null,
        };
    }

    private readonly FakeHandler _handler = new();
    private readonly HttpRequestProvider _provider;

    public HttpProviderTests() => _provider = new HttpRequestProvider(_handler, new FakeCredentials());

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    private Task Run(params (string Name, string Value)[] with) => _provider.RunAsync(with);

    [Fact]
    public async Task GetIsTheDefault()
    {
        await Run(("url", "http://localhost:47990/api/apps"));
        Assert.Equal(HttpMethod.Get, _handler.Request!.Method);
        Assert.Null(_handler.Body);
    }

    [Fact]
    public async Task PostSendsTheBodyAsJsonWhenItLooksLikeJson()
    {
        await Run(("url", "https://example.test/x"), ("method", "POST"), ("body", " {\"a\":1}"));
        Assert.Equal(HttpMethod.Post, _handler.Request!.Method);
        Assert.Equal(" {\"a\":1}", _handler.Body);
        Assert.Equal("application/json", _handler.Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task HeadersAreSentAndContentTypeGoesOnTheBody()
    {
        var headers = new MapParam(new ValueDictionary<ParamValue>(
        [
            KeyValuePair.Create("X-Thing", (ParamValue)"1"),
            KeyValuePair.Create("content-type", (ParamValue)"text/csv"),
        ]));
        var context = Steps.Context("http.request", ("url", "https://example.test/x"), ("method", "PUT"), ("body", "a,b"));
        var node = (StepNode)context.Node;
        context = context with { Node = node with { With = new ValueDictionary<ParamValue>(node.With.Append(KeyValuePair.Create("headers", (ParamValue)headers))) } };

        await _provider.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(["1"], _handler.Request!.Headers.GetValues("X-Thing"));
        Assert.Equal("text/csv", _handler.Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task CredentialWithAUserIsBasic()
    {
        await Run(("url", "https://localhost/x"), ("credential", "sunshine"));
        Assert.Equal("Basic", _handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String("admin:s3cret"u8.ToArray()), _handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task CredentialWithoutAUserIsABearerToken()
    {
        await Run(("url", "https://localhost/x"), ("credential", "token"));
        Assert.Equal("Bearer abc", _handler.Request!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task MissingCredentialSaysWhereToSaveIt()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(("url", "https://localhost/x"), ("credential", "nope")));
        Assert.Contains("CtrlAltDaze:nope", failure.Message);
        Assert.Null(_handler.Request);
    }

    [Fact]
    public async Task ErrorStatusFailsWithoutTheSecret()
    {
        _handler.Status = HttpStatusCode.Unauthorized;
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(("url", "https://localhost/x"), ("credential", "sunshine")));
        Assert.Equal("localhost answered 401 Unauthorized.", failure.Message);
        Assert.DoesNotContain("s3cret", failure.Message);
    }

    [Theory]
    [InlineData("localhost/x")]
    [InlineData("ftp://host/x")]
    public async Task OnlyWebAddressesAreSent(string url)
    {
        await Assert.ThrowsAsync<StepFailedException>(() => Run(("url", url)));
        Assert.Null(_handler.Request);
    }

    [Fact]
    public async Task UnreachableHostFailsInPlainWords()
    {
        // A port that was free a moment ago: nothing listens, so the connection is refused at once.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var real = new HttpRequestProvider(new FakeCredentials());

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => real.RunAsync(("url", $"http://127.0.0.1:{port}/")));

        Assert.StartsWith("Couldn't reach 127.0.0.1:", failure.Message);
    }

    [Fact]
    public void RealCredentialManagerReadsOnlyOurNamespace()
    {
        // Saves a throwaway generic credential with cmdkey (no admin), reads it, and always deletes it again.
        var name = "test-" + Guid.NewGuid().ToString("N");
        Cmdkey($"/generic:{WindowsCredentials.TargetPrefix}{name} /user:tester /pass:pa55");
        try
        {
            var store = new WindowsCredentials();
            Assert.Equal(new Credential("tester", "pa55"), store.Read(name));
            Assert.Null(store.Read("missing-" + name));
        }
        finally
        {
            Cmdkey($"/delete:{WindowsCredentials.TargetPrefix}{name}");
        }
    }

    [Fact]
    public void ASignInSavedUnderTheOldNameIsStillFoundAndListed()
    {
        // T29.2 (D-152): DazeControl:<name> is read when CtrlAltDaze:<name> is missing; the new one wins when both exist.
        var name = "test-" + Guid.NewGuid().ToString("N");
        Cmdkey($"/generic:{Core.OldName.CredentialPrefix}{name} /user:old /pass:pa55");
        try
        {
            var store = new WindowsCredentials();
            Assert.Equal(new Credential("old", "pa55"), store.Read(name));
            Assert.Contains(name, WindowsCredentials.Names());

            Cmdkey($"/generic:{WindowsCredentials.TargetPrefix}{name} /user:new /pass:pa55");
            Assert.Equal(new Credential("new", "pa55"), store.Read(name));
            Assert.Single(WindowsCredentials.Names(), n => n == name);
        }
        finally
        {
            Cmdkey($"/delete:{Core.OldName.CredentialPrefix}{name}");
            Cmdkey($"/delete:{WindowsCredentials.TargetPrefix}{name}");
        }
    }

    private static void Cmdkey(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new ProcessStartInfo("cmdkey.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task SignInIsNeverSentOverPlainHttpToAnotherHost()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(("url", "http://example.test/x"), ("credential", "sunshine")));
        Assert.Equal("A saved sign-in is only sent over https:// (or to this PC); example.test uses http://.", failure.Message);
        Assert.Null(_handler.Request);

        await Run(("url", "http://127.0.0.1:47990/x"), ("credential", "sunshine"));
        Assert.NotNull(_handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task ServerReasonPhraseNeverReachesTheError()
    {
        _handler.Status = (HttpStatusCode)599;
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(("url", "https://localhost/x")));
        Assert.Equal("localhost answered 599.", failure.Message);
    }

    [Fact]
    public async Task ContentHeadersGoOnTheBodyEvenWithoutOne()
    {
        var headers = new MapParam(new ValueDictionary<ParamValue>(
        [
            KeyValuePair.Create("Content-Language", (ParamValue)"de"),
            KeyValuePair.Create("Content-Type", (ParamValue)"application/x-thing"),
        ]));
        var context = Steps.Context("http.request", ("url", "https://example.test/x"), ("method", "POST"));
        var node = (StepNode)context.Node;
        context = context with { Node = node with { With = new ValueDictionary<ParamValue>(node.With.Append(KeyValuePair.Create("headers", (ParamValue)headers))) } };

        await _provider.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(["de"], _handler.Request!.Content!.Headers.ContentLanguage);
        Assert.Equal("application/x-thing", _handler.Request.Content.Headers.ContentType!.MediaType);
    }
}
