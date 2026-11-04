using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace DazeControl.Providers.Display;

/// <summary>
/// Display switching through the CCD API (port of scd's DisplayOutputsModule; D-019). Synchronous: SetDisplayConfig
/// returns once the change is applied. <paramref name="validateOnly"/> asks Windows whether it would accept the
/// change without applying it (tests on the dev PC must never leave the screens changed).
/// </summary>
public sealed unsafe class CcdDisplays(bool validateOnly = false) : IDisplays
{
    private const uint PathActive = 0x1; // DISPLAYCONFIG_PATH_ACTIVE
    private const uint ModeIdxInvalid = 0xffffffff; // DISPLAYCONFIG_PATH_MODE_IDX_INVALID

    private SET_DISPLAY_CONFIG_FLAGS Apply => validateOnly ? SET_DISPLAY_CONFIG_FLAGS.SDC_VALIDATE : SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY;

    public IReadOnlyList<DisplayOutput> Outputs() =>
        [.. ByPort(QueryAllPaths()).Select(p => p.Output).OrderBy(o => o.Port, StringComparer.OrdinalIgnoreCase)];

    public void SetTopology(DisplayTopology topology, CancellationToken cancellationToken)
    {
        var flag = topology switch
        {
            DisplayTopology.Internal => SET_DISPLAY_CONFIG_FLAGS.SDC_TOPOLOGY_INTERNAL,
            DisplayTopology.Clone => SET_DISPLAY_CONFIG_FLAGS.SDC_TOPOLOGY_CLONE,
            DisplayTopology.Extend => SET_DISPLAY_CONFIG_FLAGS.SDC_TOPOLOGY_EXTEND,
            _ => SET_DISPLAY_CONFIG_FLAGS.SDC_TOPOLOGY_EXTERNAL,
        };
        cancellationToken.ThrowIfCancellationRequested();
        Check(PInvoke.SetDisplayConfig(0, null, 0, null, flag | Apply), "switch the display mode");
    }

    public IReadOnlyList<string> UseOnly(IReadOnlyList<string> outputs, CancellationToken cancellationToken)
    {
        var all = QueryAllPaths();
        // Same order as Outputs(), which the editor's checklist matches against (D-051).
        var ports = ByPort(all).Where(p => p.Output.Available)
            .OrderBy(p => p.Output.Port, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = new List<string>();
        var chosen = new List<(DisplayOutput Output, DISPLAYCONFIG_PATH_INFO Path)>();
        foreach (var name in outputs)
        {
            var index = ScreenMatch.IndexOf(name, ports, p => p.Output.Port, p => p.Output.Monitor);
            if (index < 0)
                missing.Add(name);
            else if (!chosen.Any(c => SameTarget(c.Path, ports[index].Path)))
                chosen.Add(ports[index]);
        }
        if (missing.Count > 0)
            return missing; // change nothing when the list can't be honoured in full

        // Each screen its own source (D-077): two on one source is a duplicate, which "use only" never asks for.
        var requests = chosen.Select((c, i) => new SourceRequest(Key(c.Path.targetInfo.adapterId), c.Path.targetInfo.id, i,
            all.Where(p => (p.flags & PathActive) != 0 && SameTarget(p, c.Path)).Select(p => (uint?)p.sourceInfo.id).FirstOrDefault())).ToList();
        var sources = DisplayCheck.AssignSources(requests, Offered(all))
            ?? throw new StepFailedException("The graphics card can't show that many screens at once.");
        var paths = new DISPLAYCONFIG_PATH_INFO[chosen.Count];
        for (var i = 0; i < paths.Length; i++)
        {
            var target = chosen[i].Path;
            paths[i] = all.First(p => SameTarget(p, target) && p.sourceInfo.id == sources[i]
                && p.sourceInfo.adapterId.Equals(target.targetInfo.adapterId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        SwitchSet(paths, validateOnly, "switch the screens");
        if (!validateOnly)
            Verify([.. chosen.Select((c, i) => new ScreenWant(Name(c.Output), Identity(c.Path.targetInfo.adapterId, c.Path.targetInfo.id), i))], null);
        return [];
    }

    /// <summary>
    /// Turns on exactly these paths (their source and target ids are used; modes aren't). Windows is asked for this
    /// set's saved arrangement first (<c>SDC_TOPOLOGY_SUPPLIED</c>); only a set it has never seen is left to Windows to
    /// arrange, and then saved (X-009).
    /// </summary>
    internal static void SwitchSet(DISPLAYCONFIG_PATH_INFO[] paths, bool validateOnly, string what)
    {
        for (var i = 0; i < paths.Length; i++)
        {
            paths[i].flags |= PathActive;
            paths[i].sourceInfo.modeInfoIdx = ModeIdxInvalid;
            paths[i].targetInfo.modeInfoIdx = ModeIdxInvalid;
        }
        var apply = validateOnly ? SET_DISPLAY_CONFIG_FLAGS.SDC_VALIDATE : SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY;
        fixed (DISPLAYCONFIG_PATH_INFO* first = paths)
        {
            if (PInvoke.SetDisplayConfig((uint)paths.Length, first, 0, null, apply | SET_DISPLAY_CONFIG_FLAGS.SDC_TOPOLOGY_SUPPLIED) == 0)
                return;
            Check(PInvoke.SetDisplayConfig((uint)paths.Length, first, 0, null,
                apply | SET_DISPLAY_CONFIG_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SET_DISPLAY_CONFIG_FLAGS.SDC_ALLOW_CHANGES
                | (validateOnly ? 0 : SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE)), what);
        }
    }

    /// <summary>Reads the screens back after a write and fails the step when Windows did something else (D-077).</summary>
    internal static void Verify(IReadOnlyList<ScreenWant> wanted, string? layout)
    {
        var difference = DisplayCheck.Differences(wanted, ActiveScreens(), layout);
        if (difference is not null)
            throw new StepFailedException(difference);
    }

    /// <summary>The screens on now, each with its source mode (resolution and position).</summary>
    internal static List<ScreenState> ActiveScreens()
    {
        var (paths, modes) = CcdLayout.Query(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS);
        var screens = new List<ScreenState>(paths.Length);
        foreach (var path in paths)
        {
            var adapter = Key(path.targetInfo.adapterId);
            var index = path.sourceInfo.modeInfoIdx;
            var source = index < modes.Length && modes[index].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
                ? modes[index].Anonymous.sourceMode : default;
            screens.Add(new ScreenState(Name(Describe(path)),
                Identity(path.targetInfo.adapterId, path.targetInfo.id), adapter, path.sourceInfo.id, (int)source.width, (int)source.height, source.position.x, source.position.y));
        }
        return screens;
    }

    /// <summary>Every source → target pair Windows offers.</summary>
    internal static List<CcdPath> Offered(DISPLAYCONFIG_PATH_INFO[] all) =>
        [.. all.Select(p => new CcdPath(Key(p.sourceInfo.adapterId), p.sourceInfo.id, p.targetInfo.id)).Distinct()];

    /// <summary>What a screen is across target ids: its monitor's device path (else adapter and target).</summary>
    internal static string Identity(LUID adapter, uint target) =>
        CcdLayout.Target(adapter, target).DevicePath is { Length: > 0 } device ? device : $"{Key(adapter)}:{target}";

    internal static long Key(LUID adapter) => ((long)adapter.HighPart << 32) | adapter.LowPart;

    internal static string Name(DisplayOutput output) => output.Monitor.Length > 0 ? output.Monitor : output.Port;

    private static bool SameTarget(DISPLAYCONFIG_PATH_INFO a, DISPLAYCONFIG_PATH_INFO b) =>
        a.targetInfo.id == b.targetInfo.id && a.targetInfo.adapterId.Equals(b.targetInfo.adapterId);

    public DisplayLayout CaptureLayout(string key) => CcdLayout.Capture(key);

    public void ApplyLayout(DisplayLayout layout, CancellationToken cancellationToken) => CcdLayout.Apply(layout, validateOnly, cancellationToken);

    internal static void Check(int error, string what)
    {
        if (error != 0)
            throw new StepFailedException($"Windows couldn't {what} (error {error}); the graphics driver may not support that combination.");
    }

    /// <summary>
    /// One path per port of each adapter: the active one, else an available one (as scd chose). Connector numbers
    /// count per adapter, so a label two adapters share gets "@2", "@3" on the later adapters ("HDMI-1@2").
    /// </summary>
    internal static List<(DisplayOutput Output, DISPLAYCONFIG_PATH_INFO Path)> ByPort(DISPLAYCONFIG_PATH_INFO[] paths)
    {
        var result = new List<(DisplayOutput Output, DISPLAYCONFIG_PATH_INFO Path)>();
        foreach (var path in paths)
        {
            var output = Describe(path);
            var index = result.FindIndex(r => r.Output.Port.Equals(output.Port, StringComparison.OrdinalIgnoreCase)
                && r.Path.targetInfo.adapterId.Equals(path.targetInfo.adapterId));
            if (index < 0)
            {
                result.Add((output, path));
                continue;
            }
            // Several paths per port: one per source, and on some drivers a second target id while duplicating
            // (0x9038 and 0x1009038). Keep the lowest id, the screen's own, and its best state across them.
            var (kept, keptPath) = result[index];
            var (best, bestPath) = path.targetInfo.id < keptPath.targetInfo.id ? (output, path) : (kept, keptPath);
            result[index] = (best with { Active = kept.Active || output.Active, Available = kept.Available || output.Available }, bestPath);
        }
        var adapters = result.Select(r => r.Path.targetInfo.adapterId).Distinct().ToList();
        return [.. result.Select(r => (r.Output with
        {
            Port = Disambiguate(r.Output.Port, adapters.IndexOf(r.Path.targetInfo.adapterId),
                result.Where(o => o.Output.Port.Equals(r.Output.Port, StringComparison.OrdinalIgnoreCase))
                    .Select(o => adapters.IndexOf(o.Path.targetInfo.adapterId)).Min()),
        }, r.Path))];
    }

    internal static string Disambiguate(string port, int adapter, int firstAdapterWithThatPort) =>
        adapter == firstAdapterWithThatPort ? port : $"{port}@{adapter + 1}";

    internal static DISPLAYCONFIG_PATH_INFO[] QueryAllPaths()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var error = PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS, out var pathCount, out var modeCount);
            if (error != WIN32_ERROR.ERROR_SUCCESS)
                throw new StepFailedException($"Windows couldn't list the screens (error {(int)error}).");
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            fixed (DISPLAYCONFIG_PATH_INFO* pathArray = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* modeArray = modes)
            {
                error = PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS, &pathCount, pathArray, &modeCount, modeArray, null);
            }
            if (error == WIN32_ERROR.ERROR_SUCCESS)
                return paths[..(int)pathCount];
            if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
                throw new StepFailedException($"Windows couldn't list the screens (error {(int)error}).");
            // A screen came or went between the two calls: size again.
        }
        throw new StepFailedException("Windows couldn't list the screens: they kept changing.");
    }

    private static DisplayOutput Describe(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        request.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        request.header.adapterId = path.targetInfo.adapterId;
        request.header.id = path.targetInfo.id;
        var ok = PInvoke.DisplayConfigGetDeviceInfo(&request.header) == 0;
        var technology = ok ? request.outputTechnology : path.targetInfo.outputTechnology;
        var connector = ok ? request.connectorInstance : path.targetInfo.id;
        var monitor = ok ? request.monitorFriendlyDeviceName.ToString() : "";
        return new DisplayOutput($"{Prefix(technology)}-{connector}", monitor, path.targetInfo.targetAvailable, (path.flags & PathActive) != 0,
            ok ? request.monitorDevicePath.ToString() : "");
    }

    // scd's port names, so its profiles carry over unchanged.
    private static string Prefix(DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology) => technology switch
    {
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => "HDMI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => "DP",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED => "eDP",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI => "DVI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HD15 => "VGA",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS => "LVDS",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => "INT",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST => "MCAST",
        _ => "OUT",
    };
}
