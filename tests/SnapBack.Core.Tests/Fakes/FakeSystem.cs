using SnapBack.Core.Execution;
using SnapBack.Core.Model;

namespace SnapBack.Core.Tests.Fakes;

public enum Behavior
{
    /// <summary>Exits (with its children) when asked to close its windows.</summary>
    ExitOnClose,

    /// <summary>Hides to the tray on close; exits when asked through Restart Manager.</summary>
    HideToTrayThenExitOnRestartManager,

    /// <summary>Shows a "save changes?" prompt: keeps its window and never exits.</summary>
    PromptsUser,

    /// <summary>Declines the Restart Manager request.</summary>
    DeclinesRestartManager,

    /// <summary>Exits on Ctrl+C.</summary>
    ExitOnCtrlC,

    /// <summary>Ignores everything.</summary>
    Ignores,
}

/// <summary>In-memory simulation of Windows used to test the orchestration logic.</summary>
internal sealed class FakeSystem : ISystemProbe, IProcessController
{
    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessInfo> _processes;
    private readonly Dictionary<int, Behavior> _behaviors = new();

    public FakeSystem(IEnumerable<ProcessInfo> processes, SessionContext? ctx = null)
    {
        _processes = processes.ToDictionary(p => p.Pid);
        Context = ctx ?? TestData.Context();
    }

    public SessionContext Context { get; }

    public List<string> Calls { get; } = [];

    public int TerminateCalls => Calls.Count(c => c.StartsWith("terminate", StringComparison.Ordinal));

    /// <summary>Other programs attached to a console, keyed by the console owner PID.</summary>
    public Dictionary<int, List<(int Pid, string Name)>> ConsoleSharers { get; } = new();

    public Func<ProcessTarget, ActionResult>? CloseOverride { get; set; }

    public Behavior this[int pid]
    {
        set => _behaviors[pid] = value;
    }

    public void Kill(int pid)
    {
        lock (_gate)
        {
            RemoveTree(pid);
        }
    }

    public void Replace(ProcessInfo p)
    {
        lock (_gate)
        {
            _processes[p.Pid] = p;
        }
    }

    public bool IsAlive(int pid)
    {
        lock (_gate)
        {
            return _processes.ContainsKey(pid);
        }
    }

    // ---- ISystemProbe --------------------------------------------------------------------

    public SystemSnapshot Capture()
    {
        lock (_gate)
        {
            return TestData.Snapshot(_processes.Values.ToList(), Context);
        }
    }

    public ProcessInfo? Inspect(ProcessIdentity identity, SessionContext context)
    {
        lock (_gate)
        {
            return _processes.TryGetValue(identity.Pid, out var p) ? p : null;
        }
    }

    public MemoryStatus GetMemoryStatus() => new(16L << 30, 8L << 30);

    // ---- IProcessController ----------------------------------------------------------------

    public ActionResult RequestCloseWindows(ProcessTarget target)
    {
        lock (_gate)
        {
            Calls.Add($"close {target.Identity.Pid}");
            if (CloseOverride is not null)
            {
                return CloseOverride(target);
            }

            if (!Verify(target, out var p, out var fail))
            {
                return fail!;
            }

            switch (BehaviorOf(target.Identity.Pid))
            {
                case Behavior.ExitOnClose:
                    RemoveTree(p.Pid);
                    break;
                case Behavior.HideToTrayThenExitOnRestartManager:
                case Behavior.DeclinesRestartManager:
                    _processes[p.Pid] = p with { Windows = new WindowSummary { VisibleMainWindows = 0, HiddenTopLevelWindows = 1 } };
                    break;
            }

            return new ActionResult(ActionStatus.Done, "posted", p.Windows.VisibleMainWindows);
        }
    }

    public ActionResult InterruptConsole(ProcessTarget target, Func<int, string?, bool> isAllowedSharer)
    {
        lock (_gate)
        {
            Calls.Add($"ctrlc {target.Identity.Pid}");
            if (!Verify(target, out var p, out var fail))
            {
                return fail!;
            }

            if (ConsoleSharers.TryGetValue(p.Pid, out var sharers) && sharers.Any(s => !isAllowedSharer(s.Pid, s.Name)))
            {
                return new ActionResult(ActionStatus.SharedConsole, "shared");
            }

            if (BehaviorOf(p.Pid) == Behavior.ExitOnCtrlC)
            {
                RemoveTree(p.Pid);
            }

            return new ActionResult(ActionStatus.Done, "sent");
        }
    }

    public Task<ActionResult> ShutdownGracefullyAsync(IReadOnlyList<ProcessTarget> targets, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Calls.Add("rm " + string.Join(",", targets.Select(t => t.Identity.Pid)));
            foreach (var t in targets)
            {
                if (!Verify(t, out var p, out var fail))
                {
                    return Task.FromResult(fail!);
                }

                switch (BehaviorOf(p.Pid))
                {
                    case Behavior.DeclinesRestartManager:
                    case Behavior.PromptsUser:
                    case Behavior.Ignores:
                        return Task.FromResult(new ActionResult(ActionStatus.Declined, "declined"));
                    default:
                        RemoveTree(p.Pid);
                        break;
                }
            }

            return Task.FromResult(new ActionResult(ActionStatus.Done, "shut down"));
        }
    }

    public ProcessState GetState(ProcessIdentity identity)
    {
        lock (_gate)
        {
            return _processes.TryGetValue(identity.Pid, out var p) && p.Identity == identity ? ProcessState.Running : ProcessState.Exited;
        }
    }

    public IReadOnlyDictionary<int, WindowSummary> SnapshotWindows()
    {
        lock (_gate)
        {
            return _processes.Values.Where(p => p.Windows.HasAnyTopLevelWindow).ToDictionary(p => p.Pid, p => p.Windows);
        }
    }

    public ActionResult Terminate(ProcessTarget target)
    {
        lock (_gate)
        {
            Calls.Add($"terminate {target.Identity.Pid}");
            if (!Verify(target, out var p, out var fail))
            {
                return fail!;
            }

            RemoveTree(p.Pid);
            return new ActionResult(ActionStatus.Done, "terminated");
        }
    }

    private Behavior BehaviorOf(int pid) => _behaviors.TryGetValue(pid, out var b) ? b : Behavior.ExitOnClose;

    private bool Verify(ProcessTarget target, out ProcessInfo process, out ActionResult? failure)
    {
        process = null!;
        failure = null;
        if (!_processes.TryGetValue(target.Identity.Pid, out var p))
        {
            failure = new ActionResult(ActionStatus.Exited, "gone");
            return false;
        }

        if (p.Identity != target.Identity)
        {
            failure = new ActionResult(ActionStatus.IdentityMismatch, "pid reused");
            return false;
        }

        process = p;
        return true;
    }

    private void RemoveTree(int pid)
    {
        if (!_processes.Remove(pid, out var removed))
        {
            return;
        }

        foreach (var child in _processes.Values.Where(c => c.ParentPid == pid && c.Identity.CreationTime >= removed.Identity.CreationTime).ToList())
        {
            RemoveTree(child.Pid);
        }
    }
}
