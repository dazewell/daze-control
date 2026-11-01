using System.Diagnostics;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Process;

/// <summary>
/// <c>script.powershell</c>: runs the script in Windows PowerShell 5.1 (always installed), no profile, no window.
/// Success = exit code 0; a terminating error or <c>exit 1</c> fails the step with the last error line.
/// </summary>
public sealed class PowerShellProvider : IStepProvider
{
    private static readonly string[] Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "-"];

    public string Id => "script.powershell";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(PowerShellPath());
        foreach (var arg in Arguments)
            start.ArgumentList.Add(arg);
        // -Command - reads the script from stdin; the blank line ends its last statement. 'Stop' makes a failing
        // cmdlet fail the step instead of printing and carrying on.
        var body = "$ErrorActionPreference = 'Stop'\n" + context.RequiredText("script") + "\n\n";
        var (exitCode, lastError) = await ProcessRunner.RunAsync(start, body, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
            throw new StepFailedException(ProcessRunProvider.Failure("PowerShell", exitCode, lastError));
    }

    private static string PowerShellPath() =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
