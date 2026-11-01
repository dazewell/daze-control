using System.Diagnostics;
using System.Globalization;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Process;

/// <summary><c>process.run</c>: starts a program; when waiting, success = an exit code from <c>exitCodes</c>.</summary>
public sealed class ProcessRunProvider : IStepProvider
{
    public string Id => "process.run";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var path = Environment.ExpandEnvironmentVariables(context.RequiredText("path"));
        var start = new ProcessStartInfo(path);
        foreach (var arg in context.TextList("args"))
            start.ArgumentList.Add(Environment.ExpandEnvironmentVariables(arg));
        if (context.Text("cwd") is { Length: > 0 } cwd)
            start.WorkingDirectory = Environment.ExpandEnvironmentVariables(cwd);

        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Boolean("wait", fallback: true))
        {
            ProcessRunner.Launch(start);
            return;
        }

        var allowed = ExitCodes(context);
        var (exitCode, lastError) = await ProcessRunner.RunAsync(start, stdin: null, cancellationToken).ConfigureAwait(false);
        if (!allowed.Contains(exitCode))
            throw new StepFailedException(Failure(Path.GetFileName(path), exitCode, lastError));
    }

    internal static string Failure(string name, int exitCode, string lastError) =>
        lastError.Length == 0 ? $"{name} ended with exit code {exitCode}." : $"{name} ended with exit code {exitCode}: {lastError}";

    private static HashSet<int> ExitCodes(StepContext context)
    {
        var codes = new HashSet<int>();
        foreach (var text in context.TextList("exitCodes"))
        {
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
                throw new StepFailedException($"\"{text}\" isn't an exit code.");
            codes.Add(code);
        }
        return codes.Count == 0 ? [0] : codes;
    }
}
