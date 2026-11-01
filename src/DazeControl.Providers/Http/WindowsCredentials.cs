using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace DazeControl.Providers.Http;

/// <summary>
/// Generic credentials in Windows Credential Manager, read with <c>CredRead</c> (D-014). Only targets named
/// <c>DazeControl:&lt;name&gt;</c> are reachable, so a flow can't send the PC's other saved passwords anywhere (D-031).
/// </summary>
public sealed unsafe class WindowsCredentials : ICredentialStore
{
    public const string TargetPrefix = "DazeControl:";

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
}
