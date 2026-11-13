using System.Runtime.InteropServices;
using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Providers.Service;

/// <summary>
/// <c>devices services</c>: every Win32 service with its display name and state, read with
/// <c>EnumServicesStatusEx</c> (SC_MANAGER_ENUMERATE_SERVICE needs no admin, D-008).
/// </summary>
public static unsafe partial class ServiceList
{
    private const uint ManagerConnect = 0x0001, ManagerEnumerate = 0x0004; // SC_MANAGER_CONNECT | _ENUMERATE_SERVICE
    private const uint InfoLevelProcess = 0; // SC_ENUM_PROCESS_INFO
    private const uint TypeWin32 = 0x30; // SERVICE_WIN32
    private const uint StateAll = 0x3; // SERVICE_STATE_ALL
    private const int MoreData = 234; // ERROR_MORE_DATA

    public static List<DeviceInfo> List()
    {
        var manager = OpenSCManagerW(null, null, ManagerConnect | ManagerEnumerate);
        if (manager == 0)
            throw new Runtime.Providers.StepFailedException($"Can't list services (error {Marshal.GetLastPInvokeError()}).");
        try
        {
            var services = new List<DeviceInfo>();
            uint resume = 0;
            var buffer = new byte[64 * 1024];
            while (true)
            {
                bool done;
                uint needed, count;
                fixed (byte* bytes = buffer)
                {
                    done = EnumServicesStatusExW(manager, InfoLevelProcess, TypeWin32, StateAll, bytes, (uint)buffer.Length,
                        &needed, &count, &resume, null);
                    var error = done ? 0 : Marshal.GetLastPInvokeError();
                    if (!done && error != MoreData)
                        throw new Runtime.Providers.StepFailedException($"Can't list services (error {error}).");
                    var entries = (EnumServiceStatusProcess*)bytes;
                    for (var i = 0; i < count; i++)
                    {
                        var name = new string(entries[i].ServiceName);
                        services.Add(new DeviceInfo
                        {
                            Name = name,
                            Detail = entries[i].DisplayName is null ? null : new string(entries[i].DisplayName),
                            State = State(entries[i].CurrentState),
                            Value = name,
                        });
                    }
                }
                if (done)
                    break;
                if (needed > buffer.Length)
                    buffer = new byte[needed];
            }
            return [.. services.OrderBy(s => s.Detail ?? s.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>SERVICE_STATUS_PROCESS.dwCurrentState in the words the inspector shows.</summary>
    internal static string State(uint state) => state switch
    {
        1 => "stopped",
        4 => "running",
        7 => "paused",
        _ => "changing",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public char* ServiceName;
        public char* DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(nint manager, uint infoLevel, uint type, uint state, byte* buffer,
        uint size, uint* needed, uint* count, uint* resume, char* group);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
