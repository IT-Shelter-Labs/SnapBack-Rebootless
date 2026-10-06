using System.Runtime.InteropServices;
using SnapBack.Windows.Native;

namespace SnapBack.Windows.Tests;

/// <summary>Struct layouts must match the Windows SDK headers exactly (x64 / ARM64).</summary>
public class NativeLayoutTests
{
    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    [Fact]
    public void System_process_information_matches_winternl()
    {
        if (IntPtr.Size != 8)
        {
            return; // SnapBack ships for x64 and ARM64 only.
        }

        Assert.Equal(0x20, Offset<SYSTEM_PROCESS_INFORMATION>(nameof(SYSTEM_PROCESS_INFORMATION.CreateTime)));
        Assert.Equal(0x38, Offset<SYSTEM_PROCESS_INFORMATION>(nameof(SYSTEM_PROCESS_INFORMATION.ImageName)));
        Assert.Equal(0x50, Offset<SYSTEM_PROCESS_INFORMATION>(nameof(SYSTEM_PROCESS_INFORMATION.UniqueProcessId)));
        Assert.Equal(0x58, Offset<SYSTEM_PROCESS_INFORMATION>(nameof(SYSTEM_PROCESS_INFORMATION.InheritedFromUniqueProcessId)));
        Assert.Equal(0x64, Offset<SYSTEM_PROCESS_INFORMATION>(nameof(SYSTEM_PROCESS_INFORMATION.SessionId)));
        Assert.Equal(0x90, Offset<SYSTEM_PROCESS_INFORMATION>(nameof(SYSTEM_PROCESS_INFORMATION.WorkingSetSize)));
        Assert.Equal(0xC8, Offset<SYSTEM_PROCESS_INFORMATION>(nameof(SYSTEM_PROCESS_INFORMATION.PrivatePageCount)));
        Assert.Equal(0x100, Marshal.SizeOf<SYSTEM_PROCESS_INFORMATION>());
    }

    [Fact]
    public void Restart_manager_structs_match_rstrtmgr_h()
    {
        Assert.Equal(12, Marshal.SizeOf<RM_UNIQUE_PROCESS>());
        Assert.Equal(12 + 512 + 128 + 16, Marshal.SizeOf<RM_PROCESS_INFO>());
        Assert.Equal(12 + 512 + 128, Offset<RM_PROCESS_INFO>(nameof(RM_PROCESS_INFO.ApplicationType)));
    }

    [Fact]
    public void Memory_status_is_64_bytes() => Assert.Equal(64, Marshal.SizeOf<MEMORYSTATUSEX>());

    [Fact]
    public void Enum_service_status_process_matches_winsvc_h()
    {
        if (IntPtr.Size != 8)
        {
            return; // SnapBack ships for x64 and ARM64 only.
        }

        Assert.Equal(36, Marshal.SizeOf<SERVICE_STATUS_PROCESS>());
        Assert.Equal(56, Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>());
    }

    [Fact]
    public void Memory_structs_match_the_sdk()
    {
        Assert.Equal(16, Marshal.SizeOf<PDH_FMT_COUNTERVALUE_LARGE>());
        Assert.Equal(8, Offset<PDH_FMT_COUNTERVALUE_LARGE>(nameof(PDH_FMT_COUNTERVALUE_LARGE.LargeValue)));
        Assert.Equal(16, Marshal.SizeOf<TOKEN_PRIVILEGES_SINGLE>());
        Assert.Equal(12, Offset<TOKEN_PRIVILEGES_SINGLE>(nameof(TOKEN_PRIVILEGES_SINGLE.Attributes)));
        if (IntPtr.Size == 8)
        {
            Assert.Equal(104, Marshal.SizeOf<PERFORMANCE_INFORMATION>());
            Assert.Equal(48, Offset<PERFORMANCE_INFORMATION>(nameof(PERFORMANCE_INFORMATION.SystemCache)));
        }
    }
}
