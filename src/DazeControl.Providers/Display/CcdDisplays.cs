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
        var ports = ByPort(QueryAllPaths());
        var missing = new List<string>();
        var chosen = new List<DISPLAYCONFIG_PATH_INFO>();
        foreach (var name in outputs)
        {
            var match = ports.FirstOrDefault(p => p.Output.Available
                && (p.Output.Port.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (p.Output.Monitor.Length > 0 && p.Output.Monitor.Contains(name, StringComparison.OrdinalIgnoreCase))));
            if (match.Output is null)
            {
                missing.Add(name);
                continue;
            }
            var path = match.Path;
            path.flags |= PathActive;
            // Let Windows pick modes and positions: the database restores this set's last layout (scd behaviour).
            path.sourceInfo.modeInfoIdx = ModeIdxInvalid;
            path.targetInfo.modeInfoIdx = ModeIdxInvalid;
            if (!chosen.Any(c => c.targetInfo.id == path.targetInfo.id && c.targetInfo.adapterId.Equals(path.targetInfo.adapterId)))
                chosen.Add(path);
        }
        if (missing.Count > 0)
            return missing; // change nothing when the list can't be honoured in full

        var paths = chosen.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        fixed (DISPLAYCONFIG_PATH_INFO* first = paths)
        {
            Check(PInvoke.SetDisplayConfig((uint)paths.Length, first, 0, null,
                Apply | SET_DISPLAY_CONFIG_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG | SET_DISPLAY_CONFIG_FLAGS.SDC_ALLOW_CHANGES
                | (validateOnly ? 0 : SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE)), "switch the screens");
        }
        return [];
    }

    public DisplayLayout CaptureLayout(string key) => CcdLayout.Capture(key);

    public void ApplyLayout(DisplayLayout layout, CancellationToken cancellationToken) => CcdLayout.Apply(layout, validateOnly, cancellationToken);

    private static void Check(int error, string what)
    {
        if (error != 0)
            throw new StepFailedException($"Windows couldn't {what} (error {error}); the graphics driver may not support that combination.");
    }

    /// <summary>
    /// One path per port of each adapter: the active one, else an available one (as scd chose). Connector numbers
    /// count per adapter, so a label two adapters share gets "@2", "@3" on the later adapters ("HDMI-1@2").
    /// </summary>
    private static List<(DisplayOutput Output, DISPLAYCONFIG_PATH_INFO Path)> ByPort(DISPLAYCONFIG_PATH_INFO[] paths)
    {
        var result = new List<(DisplayOutput Output, DISPLAYCONFIG_PATH_INFO Path)>();
        foreach (var path in paths)
        {
            var output = Describe(path);
            var index = result.FindIndex(r => r.Output.Port.Equals(output.Port, StringComparison.OrdinalIgnoreCase)
                && r.Path.targetInfo.adapterId.Equals(path.targetInfo.adapterId));
            if (index < 0)
                result.Add((output, path));
            else if ((output.Active && !result[index].Output.Active)
                || (output.Available && !result[index].Output.Available && !result[index].Output.Active))
                result[index] = (output, path);
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

    private static DISPLAYCONFIG_PATH_INFO[] QueryAllPaths()
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
        return new DisplayOutput($"{Prefix(technology)}-{connector}", monitor, path.targetInfo.targetAvailable, (path.flags & PathActive) != 0);
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
