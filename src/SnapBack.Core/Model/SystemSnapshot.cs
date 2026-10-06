namespace SnapBack.Core.Model;

/// <summary>A point-in-time view of all processes, relative to SnapBack's own session.</summary>
public sealed class SystemSnapshot
{
    public SystemSnapshot(SessionContext context, IEnumerable<ProcessInfo> processes, MemoryStatus memory, DateTime takenAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(processes);

        var map = new Dictionary<int, ProcessInfo>();
        foreach (var p in processes)
        {
            // PIDs are unique within one enumeration; keep the newest entry defensively.
            if (!map.TryGetValue(p.Pid, out var existing) || existing.Identity.CreationTime < p.Identity.CreationTime)
            {
                map[p.Pid] = p;
            }
        }

        Context = context;
        Processes = map;
        Memory = memory ?? MemoryStatus.Unknown;
        TakenAtUtc = takenAtUtc;
    }

    public SessionContext Context { get; }

    public IReadOnlyDictionary<int, ProcessInfo> Processes { get; }

    public MemoryStatus Memory { get; }

    public DateTime TakenAtUtc { get; }

    /// <summary>Processes running in SnapBack's own session.</summary>
    public IEnumerable<ProcessInfo> SessionProcesses => Processes.Values.Where(p => p.SessionId == Context.SessionId);

    /// <summary>
    /// Returns the parent of <paramref name="process"/>, or <c>null</c> when the parent is gone.
    /// A process whose PID matches but which was created <em>after</em> the child cannot be its
    /// parent: that PID has been recycled.
    /// </summary>
    public ProcessInfo? ParentOf(ProcessInfo process) => ParentOf(Processes, process);

    public static ProcessInfo? ParentOf(IReadOnlyDictionary<int, ProcessInfo> processes, ProcessInfo process)
    {
        if (process.ParentPid == process.Pid || process.Pid == 0)
        {
            return null;
        }

        if (!processes.TryGetValue(process.ParentPid, out var parent))
        {
            return null;
        }

        return parent.Identity.CreationTime <= process.Identity.CreationTime ? parent : null;
    }

    /// <summary>PIDs of all live ancestors of <paramref name="pid"/> (PID-reuse aware).</summary>
    public static IReadOnlySet<int> AncestorsOf(IReadOnlyDictionary<int, ProcessInfo> processes, int pid)
    {
        var result = new HashSet<int>();
        if (!processes.TryGetValue(pid, out var current))
        {
            return result;
        }

        while (ParentOf(processes, current) is { } parent && result.Add(parent.Pid))
        {
            current = parent;
        }

        return result;
    }
}
