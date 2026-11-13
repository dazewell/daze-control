using System.ComponentModel;
using System.Diagnostics;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Process;

/// <summary>What opens a file, folder or address (the shell, with its associations); a seam for tests.</summary>
public interface IShellOpener
{
    /// <summary>Hands <paramref name="target"/> to the shell as the one thing to open, no arguments; returns when it was handed over.</summary>
    void Open(string target);
}

/// <summary>ShellExecute with the target as the file name only: it is never a command line, so nothing can be added to it (D-134).</summary>
public sealed class ShellOpener : IShellOpener
{
    public void Open(string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            throw new StepFailedException($"Nothing opens {target}: {e.Message}", e);
        }
    }
}

/// <summary><c>process.open</c>: opens a file, folder or address with the app that handles it, without waiting (D-134).</summary>
public sealed class ProcessOpenProvider(IShellOpener shell) : IStepProvider
{
    public string Id => "process.open";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        // %VAR% in the written text only: a value's % was guarded when it was filled (D-129).
        var target = StepValues.Unguard(Environment.ExpandEnvironmentVariables(context.RequiredText("target"))).Trim(' ');
        Validate(target);
        cancellationToken.ThrowIfCancellationRequested();
        shell.Open(target);
        return Task.CompletedTask;
    }

    internal static void Validate(string target)
    {
        if (target.Length == 0)
            throw new StepFailedException("There is nothing to open.");
        if (target.Any(c => char.IsControl(c) || c == '"'))
            throw new StepFailedException("What to open can't hold quotes or line breaks.");
        if (StepValues.SchemeOf(target) is { } scheme)
        {
            if (StepValues.RefusedSchemes.Contains(scheme))
                throw new StepFailedException($"Opening a {scheme}: address isn't allowed.");
            if (target.Length == scheme.Length + 1)
                throw new StepFailedException($"Nothing opens \"{target}\": the address is empty.");
            return;
        }
        // A path must exist; a bare name (notepad.exe) is left to the shell, which looks it up.
        var isPath = target.AsSpan().IndexOfAny('\\', '/', ':') >= 0;
        if (isPath && !File.Exists(target) && !Directory.Exists(target))
            throw new StepFailedException($"Couldn't find {target}.");
    }
}
