using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SnapBack.Windows.Tests;

/// <summary>
/// Starts a process with a UAC-style filtered (LUA), medium-integrity token derived from the current one.
/// CI runners run tests elevated; SnapBack (correctly) refuses to touch elevated processes, so the
/// end-to-end tests launch their target de-elevated to exercise the real close path.
/// Test-only code: the product itself never changes tokens.
/// </summary>
internal static class DeElevatedLauncher
{
    private const int TokenIntegrityLevel = 25;
    private const uint SE_GROUP_INTEGRITY = 0x20;

    public static int Start(string exe, string arguments)
    {
        const uint TOKEN_ALL_ACCESS = 0xF01FF;
        const uint DISABLE_MAX_PRIVILEGE = 0x1;
        const uint LUA_TOKEN = 0x4;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var own))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
        }

        nint token = 0;
        nint sid = 0;
        try
        {
            // The same kind of filtered token UAC gives a standard (non-elevated) session.
            if (!CreateRestrictedToken(own, DISABLE_MAX_PRIVILEGE | LUA_TOKEN, 0, 0, 0, 0, 0, 0, out token))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRestrictedToken");
            }

            if (!ConvertStringSidToSid("S-1-16-8192", out sid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSidToSid");
            }

            var label = new TOKEN_MANDATORY_LABEL { Sid = sid, Attributes = SE_GROUP_INTEGRITY };
            if (!SetTokenInformation(token, TokenIntegrityLevel, ref label, (uint)(Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(sid))))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation");
            }

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmd = new StringBuilder($"\"{exe}\" {arguments}");
            if (!CreateProcessAsUser(token, null, cmd, 0, 0, false, 0, 0, Path.GetDirectoryName(exe), ref si, out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (sid != 0)
            {
                LocalFree(sid);
            }

            if (token != 0)
            {
                CloseHandle(token);
            }

            CloseHandle(own);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(nint existingToken, uint flags, uint disableSidCount, nint sidsToDisable,
        uint deletePrivilegeCount, nint privilegesToDelete, uint restrictedSidCount, nint sidsToRestrict, out nint newToken);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSidToSid(string stringSid, out nint sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int GetLengthSid(nint sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(nint token, int tokenInformationClass, ref TOKEN_MANDATORY_LABEL info, uint length);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(nint token, string? applicationName, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint handle);
}
