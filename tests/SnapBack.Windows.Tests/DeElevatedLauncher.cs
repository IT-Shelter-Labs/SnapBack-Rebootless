using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SnapBack.Windows.Tests;

/// <summary>
/// Starts a process with a normal-user, medium-integrity token derived from the current one.
/// CI runners run tests elevated; SnapBack (correctly) refuses to touch elevated processes, so the
/// end-to-end tests launch their target de-elevated to exercise the real close path.
/// Test-only code: the product itself never changes tokens.
/// </summary>
internal static class DeElevatedLauncher
{
    private const uint SAFER_SCOPEID_USER = 2;
    private const uint SAFER_LEVELID_NORMALUSER = 0x20000;
    private const uint SAFER_LEVEL_OPEN = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint SE_GROUP_INTEGRITY = 0x20;

    public static int Start(string exe, string arguments)
    {
        if (!SaferCreateLevel(SAFER_SCOPEID_USER, SAFER_LEVELID_NORMALUSER, SAFER_LEVEL_OPEN, out var level, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SaferCreateLevel");
        }

        nint token = 0;
        nint sid = 0;
        try
        {
            if (!SaferComputeTokenFromLevel(level, 0, out token, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SaferComputeTokenFromLevel");
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

            SaferCloseLevel(level);
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
    private static extern bool SaferCreateLevel(uint scopeId, uint levelId, uint openFlags, out nint levelHandle, nint reserved);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferComputeTokenFromLevel(nint levelHandle, nint inAccessToken, out nint outAccessToken, uint flags, nint reserved);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferCloseLevel(nint levelHandle);

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
