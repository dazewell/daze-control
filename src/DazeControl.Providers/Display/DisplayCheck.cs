namespace DazeControl.Providers.Display;

/// <summary>One path Windows offers (QDC_ALL_PATHS): a source of an adapter to one of its targets.</summary>
internal readonly record struct CcdPath(long Adapter, uint Source, uint Target);

/// <summary>
/// A screen to turn on. Requests with the same <paramref name="Group"/> show one picture (a clone, only from a saved
/// layout); <paramref name="Preferred"/> is the source it has now or had when captured.
/// </summary>
internal readonly record struct SourceRequest(long Adapter, uint Target, int Group, uint? Preferred);

/// <summary>
/// A screen as Windows has it on now. <paramref name="Monitor"/> identifies it (the monitor's device path): some drivers
/// give one screen a second target id while it duplicates (seen: 0x9038 and 0x1009038), so target ids don't.
/// </summary>
internal sealed record ScreenState(string Name, string Monitor, long Adapter, uint Source, int Width, int Height, int X, int Y);

/// <summary>Where a screen should end up (<paramref name="Place"/> only for a layout: width, height, x, y).</summary>
internal sealed record ScreenWant(string Name, string Monitor, int Group, (int Width, int Height, int X, int Y)? Place = null);

/// <summary>
/// The pure halves of the display writes (D-077): which source each screen gets, and whether what Windows did matches
/// what was asked. Kept apart from the CCD calls so they're tested with fakes; nothing here touches the screens.
/// </summary>
internal static class DisplayCheck
{
    /// <summary>
    /// One source per group, distinct per adapter (two screens on one source are a duplicate, X-009), and offered by
    /// Windows for every target in the group. A group keeps its preferred source when it can. Null when the adapter
    /// hasn't enough sources for that many pictures.
    /// </summary>
    public static uint[]? AssignSources(IReadOnlyList<SourceRequest> requests, IReadOnlyList<CcdPath> offered)
    {
        var groups = requests.Select((r, i) => (Request: r, Index: i))
            .GroupBy(x => (x.Request.Group, x.Request.Adapter))
            .Select(g => (g.Key.Adapter, Targets: g.Select(x => x.Request.Target).ToList(), Indexes: g.Select(x => x.Index).ToList(),
                Preferred: g.Select(x => x.Request.Preferred).FirstOrDefault(p => p is not null)))
            .ToList();
        var used = new HashSet<(long, uint)>();
        var chosen = new uint?[groups.Count];

        bool Fits(int g, uint source) =>
            !used.Contains((groups[g].Adapter, source))
            && groups[g].Targets.All(t => offered.Contains(new CcdPath(groups[g].Adapter, source, t)));

        // Keep what each screen has first, so a working arrangement isn't reshuffled; then fill the rest lowest-first.
        for (var g = 0; g < groups.Count; g++)
            if (groups[g].Preferred is { } preferred && Fits(g, preferred))
            {
                chosen[g] = preferred;
                used.Add((groups[g].Adapter, preferred));
            }
        for (var g = 0; g < groups.Count; g++)
        {
            if (chosen[g] is not null)
                continue;
            var free = offered.Where(p => p.Adapter == groups[g].Adapter).Select(p => p.Source).Distinct().Order()
                .Cast<uint?>().FirstOrDefault(s => Fits(g, s!.Value));
            if (free is null)
                return null;
            chosen[g] = free;
            used.Add((groups[g].Adapter, free.Value));
        }

        var result = new uint[requests.Count];
        for (var g = 0; g < groups.Count; g++)
            foreach (var i in groups[g].Indexes)
                result[i] = chosen[g]!.Value;
        return result;
    }

    /// <summary>
    /// Null when the active screens are what was asked: exactly those screens on, no two on one picture unless they
    /// share a group, and for a layout (<paramref name="layout"/> names it) each one's resolution and position.
    /// Otherwise the step's failure text (UX #70).
    /// </summary>
    public static string? Differences(IReadOnlyList<ScreenWant> wanted, IReadOnlyList<ScreenState> now, string? layout = null)
    {
        const string Prefix = "Windows set up the screens differently: ";
        static bool Same(ScreenWant w, ScreenState s) => string.Equals(w.Monitor, s.Monitor, StringComparison.OrdinalIgnoreCase);

        foreach (var want in wanted)
            if (!now.Any(s => Same(want, s)))
                return $"{Prefix}{want.Name} is still off.";
        foreach (var screen in now)
            if (!wanted.Any(w => Same(w, screen)))
                return $"{Prefix}{screen.Name} is still on.";

        for (var i = 0; i < now.Count; i++)
            for (var j = i + 1; j < now.Count; j++)
            {
                if (now[i].Adapter != now[j].Adapter || now[i].Source != now[j].Source)
                    continue;
                var a = wanted.First(w => Same(w, now[i]));
                var b = wanted.First(w => Same(w, now[j]));
                if (a.Group != b.Group)
                    return $"{Prefix}{now[i].Name} and {now[j].Name} show the same picture.";
            }

        if (layout is not null)
            foreach (var want in wanted)
            {
                var screen = now.First(s => Same(want, s));
                if (want.Place is { } place && (place.Width, place.Height, place.X, place.Y) != (screen.Width, screen.Height, screen.X, screen.Y))
                    return $"{Prefix}{want.Name} isn't where {layout} puts it.";
            }
        return null;
    }
}
