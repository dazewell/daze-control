using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Http;

/// <summary>A saved sign-in: an empty <paramref name="User"/> means the secret is a bearer token.</summary>
public sealed record Credential(string User, string Secret);

/// <summary>Windows Credential Manager behind a seam (D-014, D-031).</summary>
public interface ICredentialStore
{
    /// <summary>Null when nothing is saved under that name.</summary>
    Credential? Read(string name);
}

/// <summary>
/// <c>http.request</c>: one request; success = a 2xx answer. A credential adds <c>Authorization</c> (Basic with a
/// user, Bearer without). The secret never reaches the step's error, the log or the YAML (D-014).
/// </summary>
public sealed class HttpRequestProvider(HttpMessageHandler handler, ICredentialStore credentials) : IStepProvider, IDisposable
{
    // One client for the engine's life: sockets are pooled, and an idle connection closes after a minute (D-009).
    private readonly HttpClient _client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// No cookies (a sign-in's session cookie would ride along on other flows' requests) and no redirects (one
    /// request, and a 3xx to a login page must not count as success).
    /// </summary>
    public HttpRequestProvider(ICredentialStore credentials)
        : this(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1), UseCookies = false, AllowAutoRedirect = false,
        }, credentials)
    {
    }

    public string Id => "http.request";

    public void Dispose() => _client.Dispose();

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var url = context.RequiredText("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new StepFailedException($"\"{url}\" isn't a web address (http:// or https://).");
        var method = context.Text("method") ?? "GET";
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);

        var body = context.Text("body") is { Length: > 0 } text ? text : null;
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(GuessType(body));
        }
        if (context.Node.With.TryGetValue("headers", out var headers) && headers is MapParam map)
        {
            foreach (var (header, value) in map.Entries)
            {
                // Content-Type, Content-Language, … belong to the body's headers (an empty body when there's none).
                if (header.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content ??= new ByteArrayContent([]);
                    request.Content.Headers.Remove(header);
                    if (!request.Content.Headers.TryAddWithoutValidation(header, value.ToString()))
                        throw new StepFailedException($"\"{header}\" can't be sent as a header.");
                }
                else if (!request.Headers.TryAddWithoutValidation(header, value.ToString()))
                {
                    throw new StepFailedException($"\"{header}\" can't be sent as a header.");
                }
            }
        }
        if (context.Text("credential") is { Length: > 0 } name)
        {
            // Basic is only base64 and a token goes as is: over plain http anyone on the network could read them.
            if (uri.Scheme == "http" && !uri.IsLoopback)
                throw new StepFailedException($"A saved sign-in is only sent over https:// (or to this PC); {uri.Host} uses http://.");
            var credential = credentials.Read(name)
                ?? throw new StepFailedException($"No saved sign-in called \"{name}\". Save it in Windows Credential Manager "
                    + $"as a generic credential named {WindowsCredentials.TargetPrefix}{name}.");
            request.Headers.Authorization = credential.User.Length == 0
                ? new AuthenticationHeaderValue("Bearer", credential.Secret)
                : new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.User + ":" + credential.Secret)));
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new StepFailedException($"Couldn't reach {uri.Host}: {(e.InnerException ?? e).Message}");
        }
        using (response)
        {
            context.SetNumber("status", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                // Never the server's reason phrase: it is untrusted text that could echo the token into the log.
                throw new StepFailedException($"{uri.Host} answered {(int)response.StatusCode}{StatusName(response.StatusCode)}.");
            if (context.Wants("body"))
                context.SetValue("body", await ReadStartAsync(response, context.WantsLong("body") ? CtrlAltDaze.Core.Runs.RunValues.MaxLongValueLength + 1 : 1001, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The first <paramref name="limit"/> characters of the answer (one over the value's limit, so the cut shows): never the whole body. 64 KB when a Get from text reads it (D-144), else 1,000.</summary>
    private static async Task<string> ReadStartAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[limit];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return new string(buffer, 0, read);
    }

    private static string StatusName(HttpStatusCode code) =>
        Enum.IsDefined(code) ? " " + code : "";

    private static string GuessType(string body) =>
        body.TrimStart() is ['{', ..] or ['[', ..] ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";
}
