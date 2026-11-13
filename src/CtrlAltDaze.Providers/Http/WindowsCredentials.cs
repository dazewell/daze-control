using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace CtrlAltDaze.Providers.Http;

/// <summary>
/// Generic credentials in Windows Credential Manager, read with <c>CredRead</c> (D-014). Only targets named
/// <c>CtrlAltDaze:&lt;name&gt;</c> are reachable, so a flow can't send the PC's other saved passwords anywhere (D-031).
/// </summary>
public sealed unsafe partial class WindowsCredentials : ICredentialStore
{
    public const string TargetPrefix = "CtrlAltDaze:";

    public Credential? Read(string name)
    {
        if (!PInvoke.CredRead(TargetPrefix + name, CRED_TYPE.CRED_TYPE_GENERIC, out var credential))
        {
            var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
            return error == WIN32_ERROR.ERROR_NOT_FOUND
                ? null
                : throw new Runtime.Providers.StepFailedException($"Can't read the saved sign-in \"{name}\" (error {(int)error}).");
        }
        try
        {
            var user = credential->UserName.ToString() ?? "";
            // cmdkey and the Credential Manager UI store the password as UTF-16 bytes.
            var secret = new string((char*)credential->CredentialBlob, 0, (int)credential->CredentialBlobSize / sizeof(char));
            return new Credential(user, secret);
        }
        finally
        {
            PInvoke.CredFree(credential);
        }
    }

    /// <summary>
    /// <c>devices credentials</c>: the names of the saved sign-ins a flow can use (<c>CtrlAltDaze:&lt;name&gt;</c>),
    /// never their user names or secrets (D-031, D-050).
    /// </summary>
    public static List<string> Names()
    {
        if (!CredEnumerateW(TargetPrefix + "*", 0, out var count, out var list))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == (int)WIN32_ERROR.ERROR_NOT_FOUND
                ? []
                : throw new Runtime.Providers.StepFailedException($"Can't list saved sign-ins (error {error}).");
        }
        try
        {
            var names = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var target = new string(list[i]->TargetName);
                // Read() asks for generic credentials only: offer just those.
                if (list[i]->Type == (uint)CRED_TYPE.CRED_TYPE_GENERIC
                    && target.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase) && target.Length > TargetPrefix.Length)
                    names.Add(target[TargetPrefix.Length..]);
            }
            return [.. names.Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        finally
        {
            FreeCredentials(list);
        }
    }

    /// <summary>The head of CREDENTIALW; only the target name is read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialHead
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerateW(string filter, uint flags, out uint count, out CredentialHead** credentials);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void FreeCredentials(void* buffer);
}
