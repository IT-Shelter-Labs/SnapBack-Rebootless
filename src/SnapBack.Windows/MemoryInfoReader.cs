using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SnapBack.Core.Model;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>
/// Reads physical memory statistics without administrator rights:
/// <list type="bullet">
/// <item><c>GlobalMemoryStatusEx</c> — total and available physical memory;</item>
/// <item><c>GetPerformanceInfo</c> — commit charge and system cache;</item>
/// <item>Performance counters (PDH) of the "Memory" object — standby, modified and free page lists,
/// the same numbers Task Manager and Resource Monitor show.</item>
/// </list>
/// Any source that fails simply leaves its values unknown.
/// </summary>
public sealed class MemoryInfoReader : IDisposable
{
    private static readonly string[] StandbyCounters =
    [
        @"\Memory\Standby Cache Reserve Bytes",
        @"\Memory\Standby Cache Normal Priority Bytes",
        @"\Memory\Standby Cache Core Bytes",
    ];

    private const string ModifiedCounter = @"\Memory\Modified Page List Bytes";
    private const string FreeCounter = @"\Memory\Free & Zero Page List Bytes";

    private readonly ILogger _log;
    private readonly object _gate = new();
    private nint _query;
    private nint[] _standby = [];
    private nint _modified;
    private nint _free;
    private bool _pdhFailed;

    public MemoryInfoReader(ILogger log)
    {
        _log = log;
    }

    public MemoryStatus Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!Kernel32.GlobalMemoryStatusEx(ref status))
        {
            return MemoryStatus.Unknown;
        }

        var result = new MemoryStatus((long)status.ullTotalPhys, (long)status.ullAvailPhys);

        var perf = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        if (Psapi.GetPerformanceInfo(ref perf, perf.cb))
        {
            var page = (long)perf.PageSize;
            result = result with
            {
                CommitTotalBytes = (long)perf.CommitTotal * page,
                CommitLimitBytes = (long)perf.CommitLimit * page,
                SystemCacheBytes = (long)perf.SystemCache * page,
            };
        }

        var (standby, modified, free) = ReadLists();
        return result with { StandbyBytes = standby, ModifiedBytes = modified, FreeBytes = free };
    }

    private (long? Standby, long? Modified, long? Free) ReadLists()
    {
        lock (_gate)
        {
            if (!EnsureQuery() || Pdh.PdhCollectQueryData(_query) != 0)
            {
                return (null, null, null);
            }

            long? standby = 0;
            foreach (var counter in _standby)
            {
                var value = Value(counter);
                standby = value is null ? null : standby + value;
            }

            return (standby, Value(_modified), Value(_free));
        }
    }

    private bool EnsureQuery()
    {
        if (_query != 0)
        {
            return true;
        }

        if (_pdhFailed)
        {
            return false;
        }

        try
        {
            if (Pdh.PdhOpenQuery(null, 0, out var query) != 0)
            {
                _pdhFailed = true;
                return false;
            }

            var standby = new List<nint>();
            foreach (var path in StandbyCounters)
            {
                if (Pdh.PdhAddEnglishCounter(query, path, 0, out var c) == 0)
                {
                    standby.Add(c);
                }
            }

            Pdh.PdhAddEnglishCounter(query, ModifiedCounter, 0, out _modified);
            Pdh.PdhAddEnglishCounter(query, FreeCounter, 0, out _free);
            if (standby.Count != StandbyCounters.Length)
            {
                standby.Clear(); // a partial standby sum would be misleading
            }

            _standby = standby.ToArray();
            _query = query;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.LogWarning(ex, "Performance counters are unavailable");
            _pdhFailed = true;
            return false;
        }
    }

    private static long? Value(nint counter)
    {
        if (counter == 0)
        {
            return null;
        }

        return Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_LARGE | Pdh.PDH_FMT_NOCAP100, out _, out var value) == 0 && value.CStatus == 0
            ? value.LargeValue
            : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != 0)
            {
                Pdh.PdhCloseQuery(_query);
                _query = 0;
            }
        }
    }
}
