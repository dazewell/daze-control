// Spike S2 entry. game <borderless|fso|exclusive> <seconds> <log> | menu <script> <log> [shot.bmp] [--force] [--close ms] | regcheck <log>
using System.Runtime.InteropServices;
using MenuProbe;

N.SetProcessDpiAwarenessContext(-4); // per monitor v2, like the engine
switch (args[0])
{
    case "game": return Game.Run(args[1], int.Parse(args[2]), args[3]);
    case "menu":
    {
        string? shot = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : null;
        int close = Array.IndexOf(args, "--close") is var i and >= 0 ? int.Parse(args[i + 1]) : 0;
        return Menu.Run(args[1], args[2], shot, args.Contains("--force"), close);
    }
    case "regcheck":
    {
        using var w = new StreamWriter(args[1]) { AutoFlush = true };
        int ok = 0;
        for (int k = 0; k < Menu.Keys.Length; k++)
        {
            if (N.RegisterHotKey(0, 200 + k, Menu.Keys[k].Mods, Menu.Keys[k].Vk)) { ok++; N.UnregisterHotKey(0, 200 + k); }
            else w.WriteLine($"{Menu.Keys[k].Name} still held: {Marshal.GetLastWin32Error()}");
        }
        w.WriteLine($"second process registered {ok}/{Menu.Keys.Length} keys");
        return ok == Menu.Keys.Length ? 0 : 1;
    }
}
return 2;
