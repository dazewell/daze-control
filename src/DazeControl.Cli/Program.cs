using DazeControl.Core;

namespace DazeControl.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        // Commands arrive in T3.6 (FEATURES F8).
        Console.Error.WriteLine($"{AppInfo.ProductName} CLI: no commands yet.");
        return args.Length == 0 ? 0 : 2;
    }
}
