using DazeControl.Runtime;

namespace DazeControl.Runtime.Tests;

public class RuntimeInfoTests
{
    [Fact]
    public void HasName() => Assert.Equal("runtime", RuntimeInfo.Name);
}
