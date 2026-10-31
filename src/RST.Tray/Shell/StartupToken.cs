// rst-c's own: thin Win32 token read for the hidden-start guard (SC-047 R3).

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RST.Tray.Shell;

internal static class StartupToken
{
    public static (int ElevationType, bool IsSystem) Read()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        const int tokenElevationType = 18;
        if (!GetTokenInformation(identity.AccessToken, tokenElevationType, out var type, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (type, identity.IsSystem);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass,
        out int information, int length, out int returnLength);
}
