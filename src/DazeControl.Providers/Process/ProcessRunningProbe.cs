using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Process;

/// <summary>Which programs are running, behind a seam (D-033).</summary>
public interface IProcesses
{
    /// <summary>Is a process with this name (no folder, no ".exe", any case) running?</summary>
    bool IsRunning(string name);
}

public sealed class WindowsProcesses : IProcesses
{
    public bool IsRunning(string name)
    {
        var found = System.Diagnostics.Process.GetProcessesByName(name);
        try
        {
            return found.Length > 0;
        }
        finally
        {
            foreach (var process in found)
                process.Dispose();
        }
    }
}

/// <summary>
/// <c>process.running</c>: a program is running. The name comes from <c>program</c> (a check step) or the <c>path</c>
/// of a Run program step, minus folder and ".exe" (D-081).
/// </summary>
public sealed class ProcessRunningProbe(IProcesses processes) : IFactProbe
{
    public string Fact => "process.running";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var text = context.Text("program") ?? context.Text("path");
        if (string.IsNullOrWhiteSpace(text))
            throw new StepFailedException("Choose a program.");
        var name = ProgramName(Environment.ExpandEnvironmentVariables(text));
        cancellationToken.ThrowIfCancellationRequested();
        var running = processes.IsRunning(name);
        return Task.FromResult(new FactReading(running, running ? $"{name} is running" : $"{name} isn't running"));
    }

    /// <summary>"C:\Apps\Spotify.exe" and "spotify.EXE" and "Spotify" all give what GetProcessesByName wants.</summary>
    internal static string ProgramName(string text)
    {
        var name = text.Trim().Trim('"');
        var slash = name.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (slash >= 0)
            name = name[(slash + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
