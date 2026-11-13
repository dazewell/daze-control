using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Network;

/// <summary>What one probe found. <see cref="Status"/> is the web answer's code; <see cref="Reason"/> a plain cause when it did not answer.</summary>
public sealed record ProbeResult(bool Answered, long Ms, int? Status = null, string? Reason = null);

/// <summary>The three probes behind a seam so tests touch no real network (D-140). Each one opens, asks once and closes.</summary>
public interface IReachProbe
{
    Task<ProbeResult> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);

    Task<ProbeResult> WebAsync(Uri uri, bool headOnly, TimeSpan timeout, CancellationToken cancellationToken);

    Task<ProbeResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The real probes. Nothing is kept between runs (idle = zero, D-009): the socket, the HTTP client and the ping object are
/// made for the probe and disposed right after. The wait covers name lookup, connecting and the answer's headers; the
/// caller's cancellation ends it at once and is not mistaken for a timeout.
/// </summary>
public sealed class WindowsReachProbe(Func<HttpMessageHandler>? handlerFactory = null) : IReachProbe
{
    public async Task<ProbeResult> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            return new(true, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, clock.ElapsedMilliseconds, Reason: "didn't answer in time");
        }
        catch (SocketException e)
        {
            return new(false, clock.ElapsedMilliseconds, Reason: SocketReason(e));
        }
    }

    public async Task<ProbeResult> WebAsync(Uri uri, bool headOnly, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        // No redirects (the first answer is the answer), no cookies, no pooling beyond this one request.
        using var handler = handlerFactory?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = true };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(headOnly ? HttpMethod.Head : HttpMethod.Get, uri);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            return new(true, clock.ElapsedMilliseconds, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, clock.ElapsedMilliseconds, Reason: "didn't answer in time");
        }
        catch (HttpRequestException e)
        {
            return new(false, clock.ElapsedMilliseconds, Reason: e.InnerException is SocketException s ? SocketReason(s) : "couldn't be reached");
        }
    }

    public async Task<ProbeResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var ping = new Ping();
        try
        {
            var task = IPAddress.TryParse(host, out var ip)
                ? ping.SendPingAsync(ip, (int)timeout.TotalMilliseconds)
                : ping.SendPingAsync(host, (int)timeout.TotalMilliseconds);
            using var _ = cancellationToken.Register(static p => ((Ping)p!).SendAsyncCancel(), ping);
            var reply = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new(true, reply.RoundtripTime)
                : new(false, clock.ElapsedMilliseconds, Reason: reply.Status == IPStatus.TimedOut ? "didn't answer in time" : "couldn't be reached");
        }
        catch (PingException e)
        {
            return new(false, clock.ElapsedMilliseconds, Reason: e.InnerException is SocketException s ? SocketReason(s) : "couldn't be reached");
        }
        catch (InvalidOperationException)
        {
            return new(false, clock.ElapsedMilliseconds, Reason: "couldn't be reached");
        }
    }

    private static string SocketReason(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "isn't a known name",
        SocketError.ConnectionRefused => "refused the connection",
        SocketError.TimedOut => "didn't answer in time",
        _ => "couldn't be reached",
    };
}

/// <summary>
/// <c>network.reach</c> (T24.8, D-140): one probe of an address, filled by the runner first (D-129) then normalised and
/// checked (<see cref="ReachAddress"/>): <c>host:port</c> is a TCP connect, a web address one GET or HEAD, a bare host a
/// ping. It fails with a sentence when the answer isn't what <c>is</c> says, so the failure link is the "else". Sentences
/// name the host only: never a user name, password, path or query. Nothing is held between runs (idle = zero).
/// </summary>
public sealed class NetworkReachProvider(IReachProbe probe) : IStepProvider
{
    public string Id => ProviderCatalog.ReachId;

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReachAddress.TryParse(context.Text("address"), out var target, out var problem))
            throw Fail(context, problem);
        var timeout = context.Text("timeout") is null ? ReachAddress.DefaultTimeout : context.Duration("timeout");
        if (timeout < ReachAddress.MinTimeout || timeout > ReachAddress.MaxTimeout)
            throw Fail(context, "The time to wait must be 1 s to 20 s.");
        var statuses = ReachAddress.TryStatuses(context.Text("status"))
            ?? throw Fail(context, "The answers that count must look like 200, 200-299 or 200-299, 401.");
        var wantReachable = (context.Text("is") ?? "reachable") != "notReachable";
        cancellationToken.ThrowIfCancellationRequested();

        var found = target!.Kind switch
        {
            ReachKind.Tcp => await probe.TcpAsync(target.Host, target.Port, timeout, cancellationToken).ConfigureAwait(false),
            ReachKind.Web => await probe.WebAsync(target.Uri!, context.Text("method") == "HEAD", timeout, cancellationToken).ConfigureAwait(false),
            _ => await probe.PingAsync(target.Host, timeout, cancellationToken).ConfigureAwait(false),
        };
        var name = target.Kind == ReachKind.Tcp ? $"{Bracket(target.Host)}:{target.Port}" : Bracket(target.Host);
        var reachable = found.Answered;
        string sentence;
        if (found.Answered && found.Status is { } code)
        {
            context.SetNumber("status", code);
            reachable = ReachAddress.Accepts(statuses, code);
            sentence = reachable ? $"{name} answers {code} in {found.Ms} ms" : $"{name} answered {code}, which isn't one that counts";
        }
        else
            sentence = found.Answered ? $"{name} answers in {found.Ms} ms" : $"{name} {found.Reason ?? "couldn't be reached"}";
        if (found.Answered)
            context.SetNumber("ms", found.Ms);
        context.SetValue("result", sentence); // given even when it fails: a failure link can say why
        if (reachable != wantReachable)
            throw new CheckNotMetException(wantReachable ? sentence : $"{name} is reachable.");
        context.SetSummary(sentence);
    }

    private static StepFailedException Fail(StepContext context, string sentence)
    {
        context.SetValue("result", sentence);
        return new StepFailedException(sentence);
    }

    private static string Bracket(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
}
