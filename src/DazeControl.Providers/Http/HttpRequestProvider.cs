using System.Net.Http.Headers;
using System.Text;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Http;

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

    public HttpRequestProvider(ICredentialStore credentials)
        : this(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1) }, credentials)
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

        string? contentType = null;
        if (context.Node.With.TryGetValue("headers", out var headers) && headers is MapParam map)
        {
            foreach (var (header, value) in map.Entries)
            {
                var text = value.ToString();
                if (header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    contentType = text;
                else if (!request.Headers.TryAddWithoutValidation(header, text))
                    throw new StepFailedException($"\"{header}\" can't be sent as a header.");
            }
        }
        if (context.Text("body") is { Length: > 0 } body)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? GuessType(body));
        }
        if (context.Text("credential") is { Length: > 0 } name)
        {
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
            if (!response.IsSuccessStatusCode)
                throw new StepFailedException($"{uri.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static string GuessType(string body) =>
        body.TrimStart() is ['{', ..] or ['[', ..] ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";
}
