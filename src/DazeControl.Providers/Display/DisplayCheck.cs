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
    /// Windows for every target in the group. Of the assignments that work, the one keeping the most groups on their
    /// preferred source wins (a working arrangement isn't reshuffled). Null when none works.
    /// </summary>
    /// <remarks>An exhaustive search: a handful of screens and sources, so it costs nothing next to SetDisplayConfig.</remarks>
    public static uint[]? AssignSources(IReadOnlyList<SourceRequest> requests, IReadOnlyList<CcdPath> offered)
    {
        var groups = requests.Select((r, i) => (Request: r, Index: i))
            .GroupBy(x => (x.Request.Group, x.Request.Adapter))
            .Select(g => (g.Key.Adapter, Targets: g.Select(x => x.Request.Target).ToList(), Indexes: g.Select(x => x.Index).ToList(),
                Preferred: g.Select(x => x.Request.Preferred).FirstOrDefault(p => p is not null)))
            .ToList();
        // Each group's possible sources, its preferred one first.
        var candidates = groups.Select(g => offered.Where(p => p.Adapter == g.Adapter).Select(p => p.Source).Distinct()
                .Where(s => g.Targets.All(t => offered.Contains(new CcdPath(g.Adapter, s, t))))
                .OrderBy(s => s == g.Preferred ? 0 : 1).ThenBy(s => s).ToList())
            .ToList();

        var used = new HashSet<(long, uint)>();
        var current = new uint[groups.Count];
        uint[]? best = null;
        var bestKept = -1;

        void Search(int g, int kept)
        {
            if (kept + (groups.Count - g) <= bestKept)
                return; // can't beat what's found
            if (g == groups.Count)
            {
                best = [.. current];
                bestKept = kept;
                return;
            }
            foreach (var source in candidates[g])
            {
                if (!used.Add((groups[g].Adapter, source)))
                    continue;
                current[g] = source;
                Search(g + 1, kept + (source == groups[g].Preferred ? 1 : 0));
                used.Remove((groups[g].Adapter, source));
            }
        }
        Search(0, 0);
        if (best is null)
            return null;

        var result = new uint[requests.Count];
        for (var g = 0; g < groups.Count; g++)
            foreach (var i in groups[g].Indexes)
                result[i] = best[g];
        return result;
    }

    /// <summary>
    /// Null when the active screens are what was asked: exactly those screens on, two on one picture exactly when they
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
        // The other way round: a saved duplicate whose screens came up apart.
        foreach (var group in wanted.GroupBy(w => w.Group).Where(g => g.Count() > 1))
        {
            var screens = group.Select(w => now.First(s => Same(w, s))).ToList();
            var apart = screens.FirstOrDefault(s => s.Adapter != screens[0].Adapter || s.Source != screens[0].Source);
            if (apart is not null)
                return $"{Prefix}{screens[0].Name} and {apart.Name} don't show the same picture.";
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
