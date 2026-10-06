using System.Runtime.InteropServices;

namespace SnapBack.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}

/// <summary>
/// SYSTEM_PROCESS_INFORMATION as returned by NtQuerySystemInformation(SystemProcessInformation).
/// The layout has been stable since Windows Vista (it is also what System.Diagnostics.Process uses).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_PROCESS_INFORMATION
{
    public uint NextEntryOffset;
    public uint NumberOfThreads;
    public long WorkingSetPrivateSize;
    public uint HardFaultCount;
    public uint NumberOfThreadsHighWatermark;
    public ulong CycleTime;
    public long CreateTime;
    public long UserTime;
    public long KernelTime;
    public UNICODE_STRING ImageName;
    public int BasePriority;
    public nint UniqueProcessId;
    public nint InheritedFromUniqueProcessId;
    public uint HandleCount;
    public uint SessionId;
    public nuint UniqueProcessKey;
    public nuint PeakVirtualSize;
    public nuint VirtualSize;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivatePageCount;
    public long ReadOperationCount;
    public long WriteOperationCount;
    public long OtherOperationCount;
    public long ReadTransferCount;
    public long WriteTransferCount;
    public long OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS_PROCESS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
    public uint dwProcessId;
    public uint dwServiceFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ENUM_SERVICE_STATUS_PROCESS
{
    public nint lpServiceName;
    public nint lpDisplayName;
    public SERVICE_STATUS_PROCESS ServiceStatusProcess;
}

/// <summary>RM_UNIQUE_PROCESS: PID plus start time - Restart Manager also refuses recycled PIDs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RM_UNIQUE_PROCESS
{
    public uint dwProcessId;
    public uint ProcessStartTimeLow;
    public uint ProcessStartTimeHigh;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RM_PROCESS_INFO
{
    public RM_UNIQUE_PROCESS Process;
    public fixed char strAppName[256];
    public fixed char strServiceShortName[64];
    public int ApplicationType;
    public uint AppStatus;
    public uint TSSessionId;
    public int bRestartable;
}
