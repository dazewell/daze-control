using System.Runtime.InteropServices;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace DazeControl.Providers.Display;

/// <summary>
/// Captures and re-applies a full CCD layout (positions, resolutions, rotation, refresh) — what MonitorSwitcher did
/// for scd. Saved: the active paths and modes as raw bytes plus each path's monitor device path. Applied: adapter
/// LUIDs and target ids change across reboots, so each path is re-resolved by its monitor device path first (D-019).
/// </summary>
internal static unsafe class CcdLayout
{
    private const uint PathActive = 0x1;
    private const uint ModeIdxInvalid = 0xffffffff;

    public static DisplayLayout Capture(string key)
    {
        var (paths, modes) = Query(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS);
        if (paths.Length == 0)
            throw new StepFailedException("No screen is on.");
        var monitors = paths.Select(p => Target(p.targetInfo.adapterId, p.targetInfo.id).DevicePath).ToArray();
        // A screen that went away mid-capture has no device path: such a layout could never be restored.
        if (monitors.Any(m => m.Length == 0))
            throw new StepFailedException("A screen changed while capturing; try again.");
        var bytes = new byte[sizeof(int) * 2 + paths.Length * sizeof(DISPLAYCONFIG_PATH_INFO) + modes.Length * sizeof(DISPLAYCONFIG_MODE_INFO)];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), paths.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(sizeof(int)), modes.Length);
        MemoryMarshal.AsBytes(paths.AsSpan()).CopyTo(bytes.AsSpan(sizeof(int) * 2));
        MemoryMarshal.AsBytes(modes.AsSpan()).CopyTo(bytes.AsSpan(sizeof(int) * 2 + paths.Length * sizeof(DISPLAYCONFIG_PATH_INFO)));
        return new DisplayLayout(key, [.. monitors], Convert.ToBase64String(bytes));
    }

    public static void Apply(DisplayLayout layout, bool validateOnly, CancellationToken cancellationToken)
    {
        var (paths, modes) = Decode(layout);
        if (paths.Length != layout.Monitors.Count)
            throw new StepFailedException($"The layout \"{layout.Key}\" is damaged; capture it again.");

        // Where each monitor is now: device path → (adapter, target id) among all current paths.
        var now = new Dictionary<string, (LUID Adapter, uint Target)>(StringComparer.OrdinalIgnoreCase);
        var all = CcdDisplays.QueryAllPaths();
        foreach (var path in all)
        {
            var target = Target(path.targetInfo.adapterId, path.targetInfo.id);
            // The lowest target id is the screen's own: some drivers add a second one while it duplicates (0x1009038).
            if (target.DevicePath.Length > 0 && path.targetInfo.targetAvailable
                && (!now.TryGetValue(target.DevicePath, out var seen) || path.targetInfo.id < seen.Target))
                now[target.DevicePath] = (path.targetInfo.adapterId, path.targetInfo.id);
        }

        var missing = layout.Monitors.Where(m => !now.ContainsKey(m)).ToList();
        if (missing.Count > 0)
            throw new StepFailedException($"{missing.Count} screen(s) of \"{layout.Key}\" aren't connected.");

        // Sources: the saved ids may not be free (or offered) where the screens are now, which Windows refuses with
        // error 87. Screens that shared a source when captured (a duplicate) share one again; the rest get their own.
        var names = CcdDisplays.ByPort(all).Where(p => p.Output.DevicePath.Length > 0).GroupBy(p => p.Output.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => CcdDisplays.Name(g.First().Output), StringComparer.OrdinalIgnoreCase);
        var groups = new List<(uint, int, uint)>();
        var requests = new SourceRequest[paths.Length];
        for (var i = 0; i < paths.Length; i++)
        {
            var saved = (paths[i].sourceInfo.adapterId.LowPart, paths[i].sourceInfo.adapterId.HighPart, paths[i].sourceInfo.id);
            if (!groups.Contains(saved))
                groups.Add(saved);
            var (adapter, target) = now[layout.Monitors[i]];
            requests[i] = new SourceRequest(CcdDisplays.Key(adapter), target, groups.IndexOf(saved), paths[i].sourceInfo.id);
        }
        var sources = DisplayCheck.AssignSources(requests, CcdDisplays.Offered(all))
            ?? throw new StepFailedException($"The graphics card can't show the screens of \"{layout.Key}\" at once.");

        var adapters = new Dictionary<(uint, int), LUID>();
        var targets = new Dictionary<(uint, int, uint), uint>();
        var newSources = new Dictionary<(uint, int, uint), uint>();
        var wanted = new List<ScreenWant>(paths.Length);
        for (var i = 0; i < paths.Length; i++)
        {
            var (adapter, target) = now[layout.Monitors[i]];
            ref var path = ref paths[i];
            adapters[(path.targetInfo.adapterId.LowPart, path.targetInfo.adapterId.HighPart)] = adapter;
            targets[(path.targetInfo.adapterId.LowPart, path.targetInfo.adapterId.HighPart, path.targetInfo.id)] = target;
            newSources[(path.sourceInfo.adapterId.LowPart, path.sourceInfo.adapterId.HighPart, path.sourceInfo.id)] = sources[i];
            var place = path.sourceInfo.modeInfoIdx < modes.Length
                && modes[path.sourceInfo.modeInfoIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
                ? modes[path.sourceInfo.modeInfoIdx].Anonymous.sourceMode : default;
            wanted.Add(new ScreenWant(names.GetValueOrDefault(layout.Monitors[i], $"Screen {i + 1}"), layout.Monitors[i], requests[i].Group,
                place.width > 0 ? ((int)place.width, (int)place.height, place.position.x, place.position.y) : null));
            path.sourceInfo.adapterId = adapter;
            path.sourceInfo.id = sources[i];
            path.targetInfo.adapterId = adapter;
            path.targetInfo.id = target;
            path.flags |= PathActive;
        }
        for (var i = 0; i < modes.Length; i++)
        {
            ref var mode = ref modes[i];
            var old = (mode.adapterId.LowPart, mode.adapterId.HighPart);
            if (mode.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET
                && targets.TryGetValue((old.LowPart, old.HighPart, mode.id), out var target))
                mode.id = target;
            else if (mode.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
                && newSources.TryGetValue((old.LowPart, old.HighPart, mode.id), out var source))
                mode.id = source;
            if (adapters.TryGetValue(old, out var adapter))
                mode.adapterId = adapter;
        }

        // Validate first: from some states (the old duplicate) Windows refuses a full layout with error 87 although it
        // takes it once the same screens are on. Then switch to the layout's screens first and apply it from there.
        const SET_DISPLAY_CONFIG_FLAGS Supplied = SET_DISPLAY_CONFIG_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SET_DISPLAY_CONFIG_FLAGS.SDC_ALLOW_CHANGES;
        cancellationToken.ThrowIfCancellationRequested();
        var error = Set(paths, modes, Supplied | SET_DISPLAY_CONFIG_FLAGS.SDC_VALIDATE);
        if (error == InvalidParameter)
        {
            CcdDisplays.SwitchSet([.. paths], validateOnly, $"turn on the screens of \"{layout.Key}\"");
            if (validateOnly)
                return;
            error = Set(paths, modes, Supplied | SET_DISPLAY_CONFIG_FLAGS.SDC_VALIDATE);
        }
        if (error == 0 && !validateOnly)
            error = Set(paths, modes, Supplied | SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY | SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE);
        if (error != 0)
            throw new StepFailedException($"Windows didn't accept the layout \"{layout.Key}\" (error {error}); capture it again.");
        if (!validateOnly)
            CcdDisplays.Verify(wanted, layout.Key);
    }

    private const int InvalidParameter = 87; // ERROR_INVALID_PARAMETER

    private static int Set(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, SET_DISPLAY_CONFIG_FLAGS flags)
    {
        fixed (DISPLAYCONFIG_PATH_INFO* pathArray = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* modeArray = modes)
            return PInvoke.SetDisplayConfig((uint)paths.Length, pathArray, (uint)modes.Length, modeArray, flags);
    }

    private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Decode(DisplayLayout layout)
    {
        try
        {
            var bytes = Convert.FromBase64String(layout.Data);
            var pathCount = BitConverter.ToInt32(bytes, 0);
            var modeCount = BitConverter.ToInt32(bytes, sizeof(int));
            var pathBytes = pathCount * sizeof(DISPLAYCONFIG_PATH_INFO);
            if (pathCount < 1 || modeCount < 0 || bytes.Length != sizeof(int) * 2 + pathBytes + modeCount * sizeof(DISPLAYCONFIG_MODE_INFO))
                throw new FormatException("sizes");
            var paths = MemoryMarshal.Cast<byte, DISPLAYCONFIG_PATH_INFO>(bytes.AsSpan(sizeof(int) * 2, pathBytes)).ToArray();
            var modes = MemoryMarshal.Cast<byte, DISPLAYCONFIG_MODE_INFO>(bytes.AsSpan(sizeof(int) * 2 + pathBytes)).ToArray();
            return (paths, modes);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new StepFailedException($"The layout \"{layout.Key}\" is damaged; capture it again.", e);
        }
    }

    internal static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Query(QUERY_DISPLAY_CONFIG_FLAGS which)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var error = PInvoke.GetDisplayConfigBufferSizes(which, out var pathCount, out var modeCount);
            if (error != WIN32_ERROR.ERROR_SUCCESS)
                throw new StepFailedException($"Windows couldn't list the screens (error {(int)error}).");
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            fixed (DISPLAYCONFIG_PATH_INFO* pathArray = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* modeArray = modes)
                error = PInvoke.QueryDisplayConfig(which, &pathCount, pathArray, &modeCount, modeArray, null);
            if (error == WIN32_ERROR.ERROR_SUCCESS)
                return (paths[..(int)pathCount], modes[..(int)modeCount]);
            if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
                throw new StepFailedException($"Windows couldn't list the screens (error {(int)error}).");
            // A screen came or went between the two calls: size again.
        }
        throw new StepFailedException("Windows couldn't list the screens: they kept changing.");
    }

    internal static (string Monitor, string DevicePath, DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY Technology, uint Connector, bool Ok) Target(LUID adapter, uint id)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        request.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        request.header.adapterId = adapter;
        request.header.id = id;
        var ok = PInvoke.DisplayConfigGetDeviceInfo(&request.header) == 0;
        return ok
            ? (request.monitorFriendlyDeviceName.ToString(), request.monitorDevicePath.ToString(), request.outputTechnology, request.connectorInstance, true)
            : ("", "", default, id, false);
    }
}
