using Microsoft.Extensions.Logging;
using SnapBack.Core.Classification;
using SnapBack.Core.Localization;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Safety;

namespace SnapBack.Core.Execution;

/// <summary>
/// One "return to baseline" operation: asks the selected applications to close, waits, reports, and -
/// only if the user separately authorizes it - force-terminates what is left.
/// </summary>
/// <remarks>
/// <para>Safety properties enforced here (and covered by tests):</para>
/// <list type="bullet">
/// <item>The graceful phase never calls <see cref="IProcessController.Terminate"/>.</item>
/// <item>Every process is re-checked by <see cref="SafetyGuard"/> against its live state right before any action.</item>
/// <item>Forced termination requires a <see cref="ForceAuthorization"/> covering the exact process identity,
/// and is only possible for processes that went through the graceful phase of this operation.</item>
/// <item>A failure with one process never aborts the operation for the others.</item>
/// </list>
/// </remarks>
public sealed class CleanupOperation
{
    private readonly List<ItemRun> _runs;
    private readonly SessionContext _ctx;
    private readonly ISystemProbe _probe;
    private readonly IProcessController _controller;
    private readonly SafetyGuard _guard;
    private readonly CleanupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private int _state; // 0 = new, 1 = graceful running/done

    public CleanupOperation(
        IEnumerable<PlanItem> items,
        SessionContext context,
        ISystemProbe probe,
        IProcessController controller,
        SafetyGuard guard,
        CleanupOptions options,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(items);
        _runs = items.DistinctBy(i => i.Group.Id).Select(i => new ItemRun(i)).ToList();
        _ctx = context;
        _probe = probe;
        _controller = controller;
        _guard = guard;
        _options = options;
        _time = time;
        _log = log;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public IReadOnlyList<ItemResult> Results => _runs.Select(r => r.ToResult()).ToList();

    // ------------------------------------------------------------------------------------------
    // Graceful phase
    // ------------------------------------------------------------------------------------------

    public async Task<OperationReport> RunGracefulAsync(IProgress<ItemResult>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _state, 1) != 0)
        {
            throw new InvalidOperationException("The graceful phase can only run once per operation.");
        }

        var started = _time.GetUtcNow().UtcDateTime;
        var memoryBefore = SafeMemory();
        _log.LogInformation("Operation {Id}: graceful close of {Count} app(s) started", Id, _runs.Count);

        foreach (var run in _runs)
        {
            Preflight(run);
            progress?.Report(run.ToResult());
        }

        var targetPids = _runs.SelectMany(r => r.Targets).Select(t => t.Planned.Pid).ToHashSet();
        foreach (var run in _runs.Where(r => r.IsActive))
        {
            RequestClose(run, targetPids);
            progress?.Report(run.ToResult());
        }

        using var rmCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = _time.GetUtcNow() + _options.GracefulTimeout;
        var cancelled = false;

        while (_runs.Any(r => r.IsActive))
        {
            if (_time.GetUtcNow() >= deadline)
            {
                break;
            }

            try
            {
                await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }

            var windows = SafeWindows();
            foreach (var run in _runs.Where(r => r.IsActive))
            {
                Poll(run, windows, rmCts.Token);
                progress?.Report(run.ToResult());
            }
        }

        // Stop pending Restart Manager requests and let them wind down.
        rmCts.Cancel();
        var pending = _runs.Where(r => r.RmTask is { IsCompleted: false }).Select(r => r.RmTask!).ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(_options.RestartManagerDrainTimeout, _time, CancellationToken.None)).ConfigureAwait(false);
        }

        var finalWindows = SafeWindows();
        foreach (var run in _runs.Where(r => r.IsActive))
        {
            RefreshExits(run);
            Finish(run, finalWindows, cancelled);
            progress?.Report(run.ToResult());
        }

        var report = BuildReport(OperationKind.Graceful, started, memoryBefore);
        _log.LogInformation("Operation {Id}: graceful phase finished: {Summary}", Id, report.Summary);
        return report;
    }

    private void Preflight(ItemRun run)
    {
        var item = run.Item;
        if (!item.IsSelectable)
        {
            run.Complete(ItemOutcome.Skipped, StillRunningReason.None, Loc.Format("Op_NotEligible", item.Group.DisplayName));
            _log.LogWarning("Refused ineligible item {App} ({Category}/{Level})", item.Group.DisplayName, item.Category, item.Level);
            return;
        }

        foreach (var member in item.Group.ActionableMembers)
        {
            ProcessInfo? live;
            try
            {
                live = _probe.Inspect(member.Identity, _ctx);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not inspect {Process}", member.Identity);
                run.Notes.Add(Loc.Format("Op_CouldNotInspect", member.ImageName, member.Pid));
                continue;
            }

            var decision = _guard.CheckGraceful(item, member, live, _ctx);
            switch (decision.Verdict)
            {
                case GuardVerdict.Allowed:
                    run.Targets.Add(new TargetState(member, live!));
                    break;
                case GuardVerdict.ProcessExited:
                case GuardVerdict.IdentityMismatch:
                    run.ExitedBeforeStart++;
                    break;
                default:
                    run.Notes.Add(decision.Message);
                    _log.LogWarning("Guard refused {Process} of {App}: {Verdict}", member.Identity, item.Group.DisplayName, decision.Verdict);
                    if (member.Pid == item.Group.Root.Pid)
                    {
                        run.RootRefused = true;
                    }

                    break;
            }
        }

        if (run.RootRefused)
        {
            run.Targets.Clear();
            run.Complete(ItemOutcome.Skipped, StillRunningReason.None, Loc.Get("Op_SkippedRefused"));
            return;
        }

        if (run.Targets.Count == 0)
        {
            run.Complete(run.ExitedBeforeStart > 0 ? ItemOutcome.AlreadyExited : ItemOutcome.Skipped, StillRunningReason.None,
                Loc.Get(run.ExitedBeforeStart > 0 ? "Op_AlreadyExited" : "Op_NothingSafe"));
            return;
        }

        run.Outcome = ItemOutcome.Closing;
    }

    private void RequestClose(ItemRun run, IReadOnlySet<int> operationPids)
    {
        run.StartedAt = _time.GetUtcNow();
        var group = run.Item.Group;

        foreach (var t in run.Targets)
        {
            t.GracefulAttempted = true;

            if (CloseStrategy.ShouldCloseWindows(t.Live))
            {
                var r = Safely(() => _controller.RequestCloseWindows(ProcessTarget.From(t.Planned)), t.Planned);
                LogAction("close request", t, r);
                if (r.Succeeded)
                {
                    run.CloseRequested = true;
                }
                else
                {
                    HandleRefusal(run, t, r);
                }
            }

            if (CloseStrategy.ShouldInterruptConsole(group, t.Live))
            {
                var r = Safely(() => _controller.InterruptConsole(ProcessTarget.From(t.Planned), (pid, name) => IsAllowedConsoleSharer(pid, name, operationPids)), t.Planned);
                LogAction("console interrupt", t, r);
                run.ConsoleResult = r;
                if (!r.Succeeded)
                {
                    HandleRefusal(run, t, r);
                }
            }
        }

        if (!run.CloseRequested && run.ConsoleResult is null)
        {
            var rmTargets = run.Targets.Where(t => !t.Exited && CloseStrategy.IsRestartManagerCandidate(t.Live)).ToList();
            if (rmTargets.Count > 0)
            {
                StartRestartManager(run, rmTargets, CancellationToken.None);
            }
            else
            {
                run.NoGracefulMethod = true;
            }
        }
    }

    private static bool IsAllowedConsoleSharer(int pid, string? imageName, IReadOnlySet<int> operationPids) =>
        operationPids.Contains(pid) || (imageName is not null && KnownProcesses.Shells.Contains(imageName));

    private void Poll(ItemRun run, IReadOnlyDictionary<int, WindowSummary> windows, CancellationToken rmToken)
    {
        RefreshExits(run);
        var now = _time.GetUtcNow();
        var alive = run.Targets.Where(t => !t.Exited).ToList();
        if (alive.Count == 0)
        {
            run.Complete(ItemOutcome.Closed, StillRunningReason.None, Loc.Get("Op_Closed"));
            return;
        }

        var rootPid = run.Item.Group.Root.Pid;
        var rootAlive = alive.Any(t => t.Planned.Pid == rootPid);
        if (!rootAlive && run.RootExitedAt is null)
        {
            run.RootExitedAt = now;
        }

        if (run.RmTask is { IsCompleted: true } && run.RmResult is null)
        {
            run.RmResult = run.RmTask.Result;
            _log.LogInformation("Restart Manager result for {App}: {Status} {Message}", run.Item.Group.DisplayName, run.RmResult.Status, run.RmResult.Message);
        }

        var visible = alive.Sum(t => VisibleWindows(windows, t));

        // Apps that hide to the tray on WM_CLOSE: ask again through Restart Manager.
        if (!run.RmTried && visible == 0 && now - run.StartedAt >= _options.EscalationDelay)
        {
            var rmTargets = alive.Where(t => windows.TryGetValue(t.Planned.Pid, out var w) && w.HasAnyTopLevelWindow).ToList();
            if (rmTargets.Count > 0)
            {
                StartRestartManager(run, rmTargets, rmToken);
            }
        }

        // Finish early when nothing further can happen.
        if (visible == 0 && !run.RmTried && run.NoGracefulMethod)
        {
            Finish(run, windows, cancelled: false);
        }
        else if (visible == 0 && run.RmResult is { Succeeded: false } && now - run.StartedAt >= _options.EscalationDelay)
        {
            Finish(run, windows, cancelled: false);
        }
        else if (visible == 0 && !run.RmTried && !run.CloseRequested && run.ConsoleResult is { Succeeded: false })
        {
            Finish(run, windows, cancelled: false);
        }
        else if (!rootAlive && visible == 0 && run.RmTask is null or { IsCompleted: true }
            && now - run.RootExitedAt >= _options.LeftoverGrace)
        {
            Finish(run, windows, cancelled: false);
        }
    }

    private static int VisibleWindows(IReadOnlyDictionary<int, WindowSummary> windows, TargetState t) =>
        windows.TryGetValue(t.Planned.Pid, out var w) ? w.VisibleMainWindows : 0;

    private void StartRestartManager(ItemRun run, IReadOnlyList<TargetState> targets, CancellationToken token)
    {
        run.RmTried = true;
        var processTargets = targets.Select(t => ProcessTarget.From(t.Planned)).ToList();
        _log.LogInformation("Asking {App} to exit via Restart Manager ({Count} process(es))", run.Item.Group.DisplayName, processTargets.Count);
        run.RmTask = RunRestartManagerAsync(processTargets, token);
    }

    private async Task<ActionResult> RunRestartManagerAsync(IReadOnlyList<ProcessTarget> targets, CancellationToken token)
    {
        try
        {
            return await _controller.ShutdownGracefullyAsync(targets, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ActionResult(ActionStatus.Cancelled, Loc.Get("Act_Cancelled"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restart Manager request failed");
            return new ActionResult(ActionStatus.Failed, ex.Message);
        }
    }

    private void RefreshExits(ItemRun run)
    {
        foreach (var t in run.Targets.Where(t => !t.Exited))
        {
            ProcessState state;
            try
            {
                state = _controller.GetState(t.Planned.Identity);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "State query failed for {Process}", t.Planned.Identity);
                state = ProcessState.Unknown;
            }

            if (state == ProcessState.Exited)
            {
                t.Exited = true;
            }
        }
    }

    private void Finish(ItemRun run, IReadOnlyDictionary<int, WindowSummary> windows, bool cancelled)
    {
        var alive = run.Targets.Where(t => !t.Exited).ToList();
        if (alive.Count == 0)
        {
            run.Complete(ItemOutcome.Closed, StillRunningReason.None, Loc.Get("Op_Closed"));
            return;
        }

        var rootAlive = alive.Any(t => t.Planned.Pid == run.Item.Group.Root.Pid);
        var visible = alive.Where(t => VisibleWindows(windows, t) > 0).ToList();

        StillRunningReason reason;
        if (cancelled)
        {
            reason = StillRunningReason.Cancelled;
        }
        else if (visible.Any(t => windows[t.Planned.Pid].AnyHung))
        {
            reason = StillRunningReason.NotResponding;
        }
        else if (visible.Count > 0)
        {
            reason = StillRunningReason.WaitingForUser;
        }
        else if (run.RmResult is { Status: ActionStatus.Declined })
        {
            reason = StillRunningReason.Declined;
        }
        else if (run.ConsoleResult is { Status: ActionStatus.SharedConsole })
        {
            reason = StillRunningReason.SharedConsole;
        }
        else if (run.NoGracefulMethod && !run.RmTried)
        {
            reason = StillRunningReason.NoGracefulMethod;
        }
        else
        {
            reason = StillRunningReason.DidNotExit;
        }

        var outcome = rootAlive ? ItemOutcome.StillRunning : ItemOutcome.PartiallyClosed;
        run.Complete(outcome, reason, ItemResult.Describe(outcome, reason));
        if (run.RmResult is { Succeeded: false } rm && rm.Status != ActionStatus.Cancelled)
        {
            run.Notes.Add(Loc.Format("Op_RestartManagerNote", rm.Message));
        }
    }

    private void HandleRefusal(ItemRun run, TargetState t, ActionResult r)
    {
        switch (r.Status)
        {
            case ActionStatus.Exited:
            case ActionStatus.IdentityMismatch:
                t.Exited = true;
                break;
            case ActionStatus.NotApplicable:
                break;
            default:
                run.Notes.Add($"{t.Planned.ImageName} (PID {t.Planned.Pid}): {r.Message}");
                break;
        }
    }

    /// <summary>
    /// Re-checks applications that were still running (for example because they were waiting for the
    /// user to answer a "save changes?" prompt). Never acts on anything.
    /// </summary>
    public IReadOnlyList<ItemResult> Recheck()
    {
        foreach (var run in _runs.Where(r => r.Outcome is ItemOutcome.StillRunning or ItemOutcome.PartiallyClosed or ItemOutcome.Failed))
        {
            RefreshExits(run);
            if (run.Targets.All(t => t.Exited))
            {
                var forced = run.Targets.Any(t => t.Terminated);
                run.Complete(forced ? ItemOutcome.Terminated : ItemOutcome.Closed, StillRunningReason.None, forced ? Loc.Get("Outcome_Terminated") : Loc.Get("Op_Closed"));
            }
            else if (run.Outcome == ItemOutcome.StillRunning && run.Targets.Any(t => t.Exited && t.Planned.Pid == run.Item.Group.Root.Pid))
            {
                run.Complete(ItemOutcome.PartiallyClosed, run.Reason, ItemResult.Describe(ItemOutcome.PartiallyClosed, run.Reason));
            }
        }

        return Results;
    }

    // ------------------------------------------------------------------------------------------
    // Force phase (explicitly authorized by the user)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Force-terminates the remaining processes of the given applications. Requires an authorization
    /// created from an explicit user confirmation, and only applies to processes that already went
    /// through the graceful phase of this operation.
    /// </summary>
    public Task<OperationReport> ForceTerminateAsync(
        ForceAuthorization authorization,
        IReadOnlyCollection<ProcessIdentity> groupIds,
        IProgress<ItemResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(groupIds);

        if (Volatile.Read(ref _state) == 0)
        {
            throw new InvalidOperationException("Applications must be asked to close normally before they can be force-terminated.");
        }

        var started = _time.GetUtcNow().UtcDateTime;
        var memoryBefore = SafeMemory();
        var selected = _runs.Where(r => groupIds.Contains(r.Item.Group.Id) && r.ToResult().CanForce).ToList();
        _log.LogWarning("Operation {Id}: user authorized force-termination of {Count} app(s)", Id, selected.Count);

        foreach (var run in selected)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            ForceRun(run, authorization);
            progress?.Report(run.ToResult());
        }

        var report = BuildReport(OperationKind.Force, started, memoryBefore, selected);
        return Task.FromResult(report);
    }

    private void ForceRun(ItemRun run, ForceAuthorization authorization)
    {
        var rootPid = run.Item.Group.Root.Pid;
        RefreshExits(run);
        var refused = false;

        foreach (var t in run.Targets.Where(t => !t.Exited).OrderBy(t => t.Planned.Pid == rootPid ? 0 : 1))
        {
            ProcessInfo? live;
            try
            {
                live = _probe.Inspect(t.Planned.Identity, _ctx);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not inspect {Process} before forced termination", t.Planned.Identity);
                run.Notes.Add(Loc.Format("Op_CouldNotInspectForce", t.Planned.ImageName, t.Planned.Pid));
                refused = true;
                continue;
            }

            var decision = _guard.CheckForce(run.Item, t.Planned, live, _ctx, authorization, t.GracefulAttempted, _time.GetUtcNow().UtcDateTime);
            if (!decision.Allowed)
            {
                if (decision.Verdict is GuardVerdict.ProcessExited or GuardVerdict.IdentityMismatch)
                {
                    t.Exited = true;
                    continue;
                }

                _log.LogWarning("Guard refused forced termination of {Process}: {Verdict}", t.Planned.Identity, decision.Verdict);
                run.Notes.Add(decision.Message);
                refused = true;
                continue;
            }

            var r = Safely(() => _controller.Terminate(ProcessTarget.From(t.Planned)), t.Planned);
            LogAction("forced termination", t, r);
            if (r.Succeeded)
            {
                t.Exited = true;
                t.Terminated = true;
            }
            else if (r.Status is ActionStatus.Exited or ActionStatus.IdentityMismatch)
            {
                t.Exited = true;
            }
            else
            {
                run.Notes.Add($"{t.Planned.ImageName} (PID {t.Planned.Pid}): {r.Message}");
                refused = true;
            }
        }

        // Children of a terminated root may need a moment to disappear.
        RefreshExits(run);
        var remaining = run.Targets.Count(t => !t.Exited);
        if (remaining == 0)
        {
            run.Complete(ItemOutcome.Terminated, StillRunningReason.None, ItemResult.Describe(ItemOutcome.Terminated, StillRunningReason.None));
        }
        else
        {
            run.Complete(ItemOutcome.Failed, StillRunningReason.None,
                refused ? Loc.Get("Op_SomeNotTerminated") : Loc.Format("Op_StillRunningCount", remaining));
        }
    }

    // ------------------------------------------------------------------------------------------
    // Dry run
    // ------------------------------------------------------------------------------------------

    /// <summary>Evaluates every planned step against the live safety checks without acting.</summary>
    public static DryRunReport DryRun(IEnumerable<PlanItem> items, SessionContext ctx, ISystemProbe probe, SafetyGuard guard, DateTime nowUtc)
    {
        var result = new List<DryRunItem>();
        foreach (var item in items)
        {
            if (!item.IsSelectable)
            {
                result.Add(new DryRunItem(item, new GuardDecision(GuardVerdict.ItemNotEligible, Loc.Get("Op_DryNotEligible")), []));
                continue;
            }

            var steps = new List<DryRunStep>();
            foreach (var step in item.Steps)
            {
                if (step.Kind == StepKind.LeftAlone)
                {
                    steps.Add(new DryRunStep(step, new GuardDecision(GuardVerdict.ProtectedNow, Loc.Get("Op_DryLeftAlone"))));
                    continue;
                }

                var planned = item.Group.Members.First(m => m.Identity == step.Target);
                GuardDecision decision;
                try
                {
                    decision = guard.CheckGraceful(item, planned, probe.Inspect(step.Target, ctx), ctx);
                }
                catch (Exception ex)
                {
                    decision = new GuardDecision(GuardVerdict.ItemNotEligible, Loc.Format("Op_DryCouldNotInspect", ex.Message));
                }

                steps.Add(new DryRunStep(step, decision));
            }

            var rootStep = steps.FirstOrDefault(s => s.Step.Target == item.Group.Id);
            var itemDecision = rootStep is { Decision.Allowed: false } && rootStep.Decision.Verdict is not GuardVerdict.ProcessExited
                ? rootStep.Decision
                : GuardDecision.Allow;
            result.Add(new DryRunItem(item, itemDecision, steps));
        }

        return new DryRunReport(nowUtc, result);
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private ActionResult Safely(Func<ActionResult> action, ProcessInfo target)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Action on {Process} ({Name}) failed", target.Identity, target.ImageName);
            return new ActionResult(ActionStatus.Failed, ex.Message);
        }
    }

    private IReadOnlyDictionary<int, WindowSummary> SafeWindows()
    {
        try
        {
            return _controller.SnapshotWindows();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Window enumeration failed");
            return new Dictionary<int, WindowSummary>();
        }
    }

    private MemoryStatus SafeMemory()
    {
        try
        {
            return _probe.GetMemoryStatus();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Memory status unavailable");
            return MemoryStatus.Unknown;
        }
    }

    private void LogAction(string action, TargetState t, ActionResult r) =>
        _log.LogInformation("{Action} -> {Name} {Process}: {Status} {Message}", action, t.Planned.ImageName, t.Planned.Identity, r.Status, r.Message);

    private OperationReport BuildReport(OperationKind kind, DateTime started, MemoryStatus memoryBefore, IReadOnlyList<ItemRun>? runs = null) => new()
    {
        OperationId = Id,
        Kind = kind,
        StartedAtUtc = started,
        FinishedAtUtc = _time.GetUtcNow().UtcDateTime,
        Items = (runs ?? _runs).Select(r => r.ToResult()).ToList(),
        MemoryBefore = memoryBefore,
        MemoryAfter = SafeMemory(),
    };

    private sealed class TargetState(ProcessInfo planned, ProcessInfo live)
    {
        public ProcessInfo Planned { get; } = planned;

        public ProcessInfo Live { get; } = live;

        public bool GracefulAttempted { get; set; }

        public bool Exited { get; set; }

        public bool Terminated { get; set; }
    }

    private sealed class ItemRun(PlanItem item)
    {
        public PlanItem Item { get; } = item;

        public List<TargetState> Targets { get; } = [];

        public List<string> Notes { get; } = [];

        public ItemOutcome Outcome { get; set; } = ItemOutcome.Pending;

        public StillRunningReason Reason { get; private set; }

        public string Message { get; private set; } = Loc.Get("Outcome_Pending");

        public int ExitedBeforeStart { get; set; }

        public bool RootRefused { get; set; }

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset? RootExitedAt { get; set; }

        public bool CloseRequested { get; set; }

        public ActionResult? ConsoleResult { get; set; }

        public bool NoGracefulMethod { get; set; }

        public bool RmTried { get; set; }

        public Task<ActionResult>? RmTask { get; set; }

        public ActionResult? RmResult { get; set; }

        public bool IsActive => Outcome is ItemOutcome.Pending or ItemOutcome.Closing;

        public void Complete(ItemOutcome outcome, StillRunningReason reason, string message)
        {
            Outcome = outcome;
            Reason = reason;
            Message = message;
        }

        public ItemResult ToResult()
        {
            var remaining = Targets.Where(t => !t.Exited).Select(t => t.Planned).ToList();
            var exited = Targets.Where(t => t.Exited).ToList();
            return new ItemResult
            {
                Item = Item,
                Outcome = Outcome,
                Reason = Reason,
                Message = Outcome == ItemOutcome.Closing ? ItemResult.Describe(Outcome, Reason) : Message,
                ProcessesExited = exited.Count,
                ProcessesTerminated = exited.Count(t => t.Terminated),
                ProcessesRemaining = remaining.Count,
                MemoryFreedBytes = exited.Sum(t => t.Planned.PrivateWorkingSet),
                RemainingProcesses = remaining,
                Notes = Notes.ToArray(),
            };
        }
    }
}
