using DazeControl.Ipc;
using DazeControl.Providers;

namespace DazeControl.Providers.Tests;

public class SmokeTests
{
    [Fact]
    public void HasName() => Assert.Equal("providers", ProvidersInfo.Name);

    [Fact]
    public void PipeNameIncludesSid() => Assert.Equal("DazeControl-S-1-5-21-1", Protocol.PipeName("S-1-5-21-1"));
}
