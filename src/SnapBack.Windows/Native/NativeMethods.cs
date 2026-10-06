using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SnapBack.Windows.Native;

// All P/Invoke declarations use source-generated marshalling (LibraryImport): no runtime IL stubs,
// explicit SetLastError, and blittable signatures where possible.

internal static partial class Kernel32
{
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x0010_0000;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    public const int ProcessProtectionLevelInfo = 7;
    public const uint PROTECTION_LEVEL_NONE = 0xFFFFFFFE;

    public const uint CTRL_C_EVENT = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeProcessHandle hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessCritical(SafeProcessHandle hProcess, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetProcessInformation(SafeProcessHandle hProcess, int processInformationClass, void* processInformation, uint processInformationSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static unsafe partial int GetPackageFullName(SafeProcessHandle hProcess, ref uint packageFullNameLength, char* packageFullName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static unsafe partial uint GetConsoleProcessList(uint* lpdwProcessList, uint dwProcessCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleCtrlHandler(nint handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}

internal static partial class Ntdll
{
    public const int SystemProcessInformation = 5;
    public const int ProcessCommandLineInformation = 60;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint systemInformationLength, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, void* processInformation, uint processInformationLength, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int RtlNtStatusToDosError(int status);
}

internal static partial class Advapi32
{
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenUser = 1;
    public const int TokenElevation = 20;
    public const int TokenIntegrityLevel = 25;

    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const int SC_ENUM_PROCESS_INFO = 0;
    public const uint SERVICE_WIN32 = 0x30;
    public const uint SERVICE_USER_SERVICE = 0x40;
    public const uint SERVICE_ACTIVE = 0x1;
    public const int ERROR_MORE_DATA = 234;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeKernelHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetTokenInformation(SafeKernelHandle tokenHandle, int tokenInformationClass, void* tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumServicesStatusEx(SafeServiceHandle hSCManager, int infoLevel, uint serviceType, uint serviceState,
        byte* services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint hSCObject);
}

internal static partial class User32
{
    public const uint WM_CLOSE = 0x0010;
    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x0000_0080;
    public const long WS_EX_APPWINDOW = 0x0004_0000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong32(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static unsafe partial int GetWindowText(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    public static unsafe partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsHungAppWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    public static long GetExtendedStyle(nint hWnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE) : GetWindowLong32(hWnd, GWL_EXSTYLE);
}

internal static partial class Dwmapi
{
    public const uint DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out int pvAttribute, uint cbAttribute);
}

internal static partial class RestartManager
{
    public const int CCH_RM_SESSION_KEY = 32;
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FAIL_NOACTION_REBOOT = 350;
    public const int ERROR_FAIL_SHUTDOWN = 351;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_CANCELLED = 1223;

    public const int RmUnknownApp = 0;
    public const int RmMainWindow = 1;
    public const int RmOtherWindow = 2;
    public const int RmService = 3;
    public const int RmExplorer = 4;
    public const int RmConsole = 5;
    public const int RmCritical = 1000;

    [LibraryImport("rstrtmgr.dll")]
    public static unsafe partial int RmStartSession(out uint pSessionHandle, uint dwSessionFlags, char* strSessionKey);

    [LibraryImport("rstrtmgr.dll")]
    public static unsafe partial int RmRegisterResources(uint dwSessionHandle, uint nFiles, nint rgsFilenames, uint nApplications, RM_UNIQUE_PROCESS* rgApplications, uint nServices, nint rgsServiceNames);

    [LibraryImport("rstrtmgr.dll")]
    public static unsafe partial int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, RM_PROCESS_INFO* rgAffectedApps, out uint lpdwRebootReasons);

    /// <summary>Note: SnapBack never passes RmForceShutdown (0x1) - applications are never forced.</summary>
    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmShutdown(uint dwSessionHandle, uint lActionFlags, nint fnStatus);

    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmCancelCurrentTask(uint dwSessionHandle);

    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmEndSession(uint dwSessionHandle);
}

/// <summary>A generic kernel handle closed with CloseHandle (used for tokens).</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

/// <summary>Service Control Manager handle.</summary>
internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeServiceHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => Advapi32.CloseServiceHandle(handle);
}
