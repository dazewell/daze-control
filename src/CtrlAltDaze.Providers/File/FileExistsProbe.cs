using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Files;

/// <summary><c>file.exists</c>: the file or folder in <c>path</c> is there (<c>file.check</c>).</summary>
public sealed class FileExistsProbe : IFactProbe
{
    public string Fact => "file.exists";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var path = Environment.ExpandEnvironmentVariables(context.RequiredText("path"));
        cancellationToken.ThrowIfCancellationRequested();
        var exists = System.IO.File.Exists(path) || Directory.Exists(path);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } leaf ? leaf : path;
        return Task.FromResult(new FactReading(exists, exists ? $"{name} exists" : $"{name} doesn't exist"));
    }
}
