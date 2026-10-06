using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SnapBack.Core.Memory;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>
/// Purges the standby list with <c>NtSetSystemInformation(SystemMemoryListInformation, …)</c> — the
/// mechanism Sysinternals RAMMap uses for "Empty Standby List". Requires an elevated process holding
/// <c>SeProfileSingleProcessPrivilege</c>, which this class enables on its own token only.
/// </summary>
/// <remarks>
/// Standby pages hold cached data that is not in use; purging moves them to the free list. Nothing
/// that a process is using is touched: working sets, the modified list (data waiting to be written)
/// and committed memory are left alone. Only the commands allowed by
/// <see cref="MemoryListCommand.IsAllowed"/> can ever be sent.
/// </remarks>
public sealed class StandbyListPurger : IStandbyPurger
{
    private readonly ILogger _log;

    public StandbyListPurger(ILogger log)
    {
        _log = log;
    }

    public unsafe DeepCleanupOutcome Purge(DeepCleanupMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            return DeepCleanupOutcome.For(DeepCleanupStatus.BadRequest);
        }

        var command = MemoryListCommand.For(mode);
        if (!MemoryListCommand.IsAllowed(command))
        {
            return DeepCleanupOutcome.For(DeepCleanupStatus.BadRequest);
        }

        if (!EnableProfilePrivilege())
        {
            _log.LogWarning("SeProfileSingleProcessPrivilege is not available");
            return DeepCleanupOutcome.For(DeepCleanupStatus.PrivilegeMissing);
        }

        var status = MemoryLists.NtSetSystemInformation(MemoryLists.SystemMemoryListInformation, &command, sizeof(int));
        _log.LogInformation("Standby purge ({Mode}, command {Command}) returned 0x{Status:X8}", mode, command, status);
        return status switch
        {
            0 => DeepCleanupOutcome.For(DeepCleanupStatus.Completed),
            MemoryLists.STATUS_PRIVILEGE_NOT_HELD => DeepCleanupOutcome.For(DeepCleanupStatus.PrivilegeMissing),
            _ => DeepCleanupOutcome.For(DeepCleanupStatus.Failed, status),
        };
    }

    private static bool EnableProfilePrivilege()
    {
        if (!MemoryLists.OpenProcessToken(MemoryLists.GetCurrentProcess(), MemoryLists.TOKEN_ADJUST_PRIVILEGES | MemoryLists.TOKEN_QUERY, out var token))
        {
            token.Dispose();
            return false;
        }

        using (token)
        {
            if (!MemoryLists.LookupPrivilegeValue(null, MemoryLists.SeProfileSingleProcessPrivilege, out var luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES_SINGLE { PrivilegeCount = 1, Luid = luid, Attributes = MemoryLists.SE_PRIVILEGE_ENABLED };
            if (!MemoryLists.AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0))
            {
                return false;
            }

            return Marshal.GetLastPInvokeError() != MemoryLists.ERROR_NOT_ALL_ASSIGNED;
        }
    }
}
