using System.Diagnostics;
using DazeControl.Providers.Process;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class ProcessProviderTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task ZeroExitSucceeds() =>
        await new ProcessRunProvider().RunAsync(("path", Cmd), ("args", "/c exit 0"));

    [Fact]
    public async Task OtherExitFailsWithTheLastErrorLine()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => new ProcessRunProvider().RunAsync(("path", Cmd), ("args", "/c echo boom 1>&2 & exit 3")));
        Assert.Equal("cmd.exe ended with exit code 3: boom", failure.Message);
    }

    [Fact]
    public async Task ListedExitCodesSucceed() =>
        await new ProcessRunProvider().RunAsync(("path", Cmd), ("args", "/c exit 3"), ("exitCodes", "3"));

    [Fact]
    public async Task MissingProgramFailsPlainly()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => new ProcessRunProvider().RunAsync(("path", @"C:\nope\missing.exe")));
        Assert.StartsWith(@"Couldn't start C:\nope\missing.exe", failure.Message);
    }

    [Fact]
    public async Task CancelKillsTheProcess()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        var context = Steps.Context("process.run", ("path", Cmd), ("args", "/c ping -n 30 127.0.0.1 >nul"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunProvider().ExecuteAsync(context, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PowerShellSucceeds() =>
        await new PowerShellProvider().RunAsync(("script", "$x = 1 + 1\nif ($x -ne 2) { exit 1 }"));

    [Fact]
    public async Task PowerShellErrorFailsTheStep()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => new PowerShellProvider().RunAsync(("script", "throw 'no headphones'")));
        Assert.Contains("no headphones", failure.Message);
    }

    [Fact]
    public async Task PowerShellStopsAtTheFirstError()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"dazecontrol-{Guid.NewGuid():N}.txt");
        await Assert.ThrowsAsync<StepFailedException>(() => new PowerShellProvider().RunAsync(
            ("script", $"throw 'first'\nSet-Content -LiteralPath '{marker}' -Value x")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ExitDoesNotWaitForADescendantHoldingThePipes()
    {
        // cmd exits at once; its detached ping child keeps the inherited stdout open for ~20 s.
        var watch = Stopwatch.StartNew();
        await new ProcessRunProvider().RunAsync(("path", Cmd), ("args", "/c start /b ping -n 20 127.0.0.1 & exit 0"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");
    }

    [Fact]
    public async Task OutputWithoutNewlinesIsDrained()
    {
        // 8 MB on one line: must be read in chunks and thrown away, not buffered as a line.
        await new PowerShellProvider().RunAsync(("script", "[Console]::Out.Write('x' * 8MB)"));
    }
}
