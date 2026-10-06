using Microsoft.Extensions.Logging;
using SnapBack.Core.Execution;
using SnapBack.Core.Model;
using SnapBack.Core.Text;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>
/// Reads processes, windows and memory using documented Win32 APIs plus NtQuerySystemInformation.
/// Requires no administrator rights. A process that cannot be opened is still reported, with its
/// unknown attributes left null so that the classifier protects it.
/// </summary>
public sealed class WindowsSystemProbe : ISystemProbe
{
    private static readonly TimeSpan ServiceCacheLifetime = TimeSpan.FromSeconds(5);

    private readonly ILogger _log;
    private readonly SessionContext _baseContext;
    private readonly FileMetadataCache _metadata = new();
    private readonly MemoryInfoReader _memory;
    private readonly object _serviceGate = new();
    private (HashSet<int> Pids, bool Ok, DateTime At) _services;

    public WindowsSystemProbe(ILogger<WindowsSystemProbe> log)
    {
        _log = log;
        _baseContext = WindowsSessionInfo.Create();
        _memory = new MemoryInfoReader(log);
    }

    public SessionContext BaseContext => _baseContext;

    public SystemSnapshot Capture()
    {
        var raw = SystemProcessInformationReader.Query();
        var (services, servicesOk) = Services(forceRefresh: true);
        var windows = SafeWindows();

        var processes = new List<ProcessInfo>(raw.Count);
        foreach (var r in raw)
        {
            processes.Add(r.SessionId == _baseContext.SessionId
                ? Enrich(r, services, windows)
                : Minimal(r, services));
        }

        var map = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var ctx = _baseContext with
        {
            SelfAncestorPids = SystemSnapshot.AncestorsOf(map, _baseContext.SelfPid),
            ServicesKnown = servicesOk,
        };

        return new SystemSnapshot(ctx, processes, GetMemoryStatus(), DateTime.UtcNow);
    }

    public ProcessInfo? Inspect(ProcessIdentity identity, SessionContext context)
    {
        var raw = SystemProcessInformationReader.Query().FirstOrDefault(r => r.Pid == identity.Pid);
        if (raw is null || raw.CreateTime != identity.CreationTime)
        {
            return null;
        }

        var (services, _) = Services(forceRefresh: false);
        var windows = SafeWindows();
        return raw.SessionId == _baseContext.SessionId ? Enrich(raw, services, windows) : Minimal(raw, services);
    }

    public MemoryStatus GetMemoryStatus() => _memory.Read();

    private static ProcessInfo Minimal(RawProcess r, HashSet<int> services) => new()
    {
        Identity = new ProcessIdentity(r.Pid, r.CreateTime),
        ParentPid = r.ParentPid,
        ImageName = r.ImageName,
        SessionId = r.SessionId,
        IsServiceHost = services.Contains(r.Pid),
        PrivateWorkingSet = r.PrivateWorkingSet,
        PrivateBytes = r.PrivateBytes,
        WorkingSet = r.WorkingSet,
    };

    private ProcessInfo Enrich(RawProcess r, HashSet<int> services, Dictionary<int, WindowSummary> windows)
    {
        var info = Minimal(r, services) with
        {
            Windows = windows.TryGetValue(r.Pid, out var w) ? w : WindowSummary.None,
        };

        if (r.Pid is 0 or 4)
        {
            return info;
        }

        try
        {
            using var h = ProcessQueries.Open(r.Pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
            if (h is null)
            {
                return info;
            }

            // The PID may have been recycled between the enumeration and OpenProcess: verify before trusting anything.
            if (ProcessQueries.CreationTime(h) != r.CreateTime)
            {
                return info;
            }

            var path = ProcessQueries.ImagePath(h);
            var token = ProcessQueries.Token(h);
            var meta = path is null ? null : _metadata.Get(path);
            string? hint = null;
            if (CommandLineHint.IsInteresting(r.ImageName))
            {
                hint = CommandLineHint.FromCommandLine(ProcessQueries.CommandLine(h));
            }

            return info with
            {
                CouldOpen = true,
                ImagePath = path,
                OwnerSid = token.OwnerSid,
                IsElevated = token.IsElevated,
                Integrity = token.Integrity,
                IsCritical = ProcessQueries.IsCritical(h),
                IsProtectedProcess = ProcessQueries.IsProtected(h),
                PackageFullName = ProcessQueries.PackageFullName(h),
                FileDescription = meta?.Description,
                ProductName = meta?.Product,
                CompanyName = meta?.Company,
                Subsystem = meta?.Subsystem ?? ImageSubsystem.Unknown,
                CommandLineHint = hint,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogDebug(ex, "Could not inspect PID {Pid} ({Name})", r.Pid, r.ImageName);
            return info;
        }
    }

    private (HashSet<int> Pids, bool Ok) Services(bool forceRefresh)
    {
        lock (_serviceGate)
        {
            if (forceRefresh || _services.Pids is null || DateTime.UtcNow - _services.At > ServiceCacheLifetime)
            {
                var (pids, ok) = ServiceProcessMap.Query(_log);
                _services = (pids, ok, DateTime.UtcNow);
            }

            return (_services.Pids, _services.Ok);
        }
    }

    private Dictionary<int, WindowSummary> SafeWindows()
    {
        try
        {
            return WindowEnumerator.SummarizeAll();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Window enumeration failed");
            return new Dictionary<int, WindowSummary>();
        }
    }
}
