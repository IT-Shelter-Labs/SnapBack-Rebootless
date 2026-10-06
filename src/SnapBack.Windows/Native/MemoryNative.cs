using System.Runtime.InteropServices;

namespace SnapBack.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE_LARGE
{
    public uint CStatus;
    public uint Padding;
    public long LargeValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES_SINGLE
{
    public uint PrivilegeCount;
    public LUID Luid;
    public uint Attributes;
}

internal static partial class Psapi
{
    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);
}

internal static partial class Pdh
{
    public const uint PDH_FMT_LARGE = 0x00000400;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE_LARGE value);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}

internal static partial class MemoryLists
{
    /// <summary>SYSTEM_INFORMATION_CLASS.SystemMemoryListInformation.</summary>
    public const int SystemMemoryListInformation = 80;
    public const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    public const int ERROR_NOT_ALL_ASSIGNED = 1300;
    public const string SeProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtSetSystemInformation(int systemInformationClass, void* systemInformation, uint systemInformationLength);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeKernelHandle tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(SafeKernelHandle tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES_SINGLE newState, uint bufferLength, nint previousState, nint returnLength);
}
