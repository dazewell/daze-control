using System.Diagnostics;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Process;

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
        // -Command - runs stdin statement by statement, so an error would end only its own statement: one try block
        // makes the first error (cmdlet errors too, via 'Stop') end the whole script with exit code 1. The blank
        // line ends the block.
        // UTF-8 out, so an output value (and an error line) keeps non-ASCII text, like a device name.
        var body = "try {\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n$ErrorActionPreference = 'Stop'\n" + context.RequiredText("script")
            + "\n} catch {\n[Console]::Error.WriteLine($_.ToString())\nexit 1\n}\n\n";
        // Earlier steps' values are data in the environment, never part of the script text (DESIGN §4.8).
        foreach (var (name, value) in context.Variables ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        var (exitCode, lastError, output) = await ProcessRunner.RunAsync(start, body, keepOutput: context.Wants("output"),
            cancellationToken, ProcessRunner.ValueTail).ConfigureAwait(false);
        context.SetNumber("exitCode", exitCode);
        if (context.Wants("output"))
            context.SetValue("output", output);
        if (exitCode != 0)
            throw new StepFailedException(ProcessRunProvider.Failure("PowerShell", exitCode, lastError));
    }

    private static string PowerShellPath() =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
