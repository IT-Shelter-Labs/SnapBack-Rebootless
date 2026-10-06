using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>PIDs of processes that host running Windows services (read-only SCM query).</summary>
internal static unsafe class ServiceProcessMap
{
    public static (HashSet<int> Pids, bool Ok) Query(ILogger log)
    {
        var pids = new HashSet<int>();
        using var scm = Advapi32.OpenSCManager(null, null, Advapi32.SC_MANAGER_CONNECT | Advapi32.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm.IsInvalid)
        {
            log.LogWarning("Could not open the Service Control Manager (error {Error})", Marshal.GetLastPInvokeError());
            return (pids, false);
        }

        // Per-user services are included where the OS supports it; fall back to classic Win32 services.
        if (Enumerate(scm, Advapi32.SERVICE_WIN32 | Advapi32.SERVICE_USER_SERVICE, pids) || Enumerate(scm, Advapi32.SERVICE_WIN32, pids))
        {
            return (pids, true);
        }

        log.LogWarning("Could not enumerate services (error {Error})", Marshal.GetLastPInvokeError());
        return (pids, false);
    }

    private static bool Enumerate(SafeServiceHandle scm, uint serviceType, HashSet<int> pids)
    {
        uint resume = 0;
        var size = 64 * 1024u;
        for (var guard = 0; guard < 64; guard++)
        {
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                var ok = Advapi32.EnumServicesStatusEx(scm, Advapi32.SC_ENUM_PROCESS_INFO, serviceType, Advapi32.SERVICE_ACTIVE,
                    buffer, size, out var needed, out var count, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastPInvokeError();
                if (!ok && error != Advapi32.ERROR_MORE_DATA)
                {
                    return false;
                }

                var entries = (ENUM_SERVICE_STATUS_PROCESS*)buffer;
                for (var i = 0; i < count; i++)
                {
                    var pid = (int)entries[i].ServiceStatusProcess.dwProcessId;
                    if (pid != 0)
                    {
                        pids.Add(pid);
                    }
                }

                if (ok)
                {
                    return true;
                }

                if (count == 0)
                {
                    size = Math.Max(size * 2, needed);
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return false;
    }
}
