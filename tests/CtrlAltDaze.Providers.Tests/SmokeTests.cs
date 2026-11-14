using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Providers.Tests;

public class SmokeTests
{
    [Fact]
    public void PipeNameIncludesSid() => Assert.Equal("CtrlAltDaze-S-1-5-21-1", Protocol.PipeName("S-1-5-21-1"));
}
