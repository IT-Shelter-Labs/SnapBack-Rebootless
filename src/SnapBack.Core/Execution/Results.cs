using SnapBack.Core.Model;
using SnapBack.Core.Localization;
using SnapBack.Core.Planning;
using SnapBack.Core.Safety;
using SnapBack.Core.Text;

namespace SnapBack.Core.Execution;

public enum ItemOutcome
{
    Pending,
    Closing,

    /// <summary>All processes of the app exited after being asked to.</summary>
    Closed,

    /// <summary>The main process exited, but some of its processes are still running.</summary>
    PartiallyClosed,

    /// <summary>The app did not exit. Nothing was forced.</summary>
    StillRunning,

    /// <summary>Force-terminated after explicit user confirmation.</summary>
    Terminated,

    /// <summary>Refused by the safety checks; nothing was done.</summary>
    Skipped,

    /// <summary>An action failed.</summary>
    Failed,

    /// <summary>The app had already exited before SnapBack acted.</summary>
    AlreadyExited,
}

public enum StillRunningReason
{
    None,

    /// <summary>The app still shows windows - most likely a "save changes?" prompt waiting for the user.</summary>
    WaitingForUser,

    NotResponding,

    /// <summary>The app declined the request to exit.</summary>
    Declined,

    /// <summary>There is no polite way to ask this process to exit.</summary>
    NoGracefulMethod,

    /// <summary>The console is shared with other programs, so Ctrl+C was not sent.</summary>
    SharedConsole,

    DidNotExit,

    Cancelled,
}

/// <summary>Immutable snapshot of one application's progress or result.</summary>
public sealed record ItemResult
{
    public required PlanItem Item { get; init; }

    public required ItemOutcome Outcome { get; init; }

    public StillRunningReason Reason { get; init; }

    public required string Message { get; init; }

    public int ProcessesExited { get; init; }

    public int ProcessesRemaining { get; init; }

    public int ProcessesTerminated { get; init; }

    public long MemoryFreedBytes { get; init; }

    public IReadOnlyList<ProcessInfo> RemainingProcesses { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsFinished => Outcome is not (ItemOutcome.Pending or ItemOutcome.Closing);

    /// <summary>Whether the user may choose to force-terminate what is left.</summary>
    public bool CanForce => Outcome is ItemOutcome.StillRunning or ItemOutcome.PartiallyClosed or ItemOutcome.Failed
        && RemainingProcesses.Count > 0;

    public static string Describe(ItemOutcome outcome, StillRunningReason reason) => outcome switch
    {
        ItemOutcome.StillRunning => Loc.Get(reason switch
        {
            StillRunningReason.WaitingForUser => "Still_WaitingForUser",
            StillRunningReason.NotResponding => "Still_NotResponding",
            StillRunningReason.Declined => "Still_Declined",
            StillRunningReason.NoGracefulMethod => "Still_NoGracefulMethod",
            StillRunningReason.SharedConsole => "Still_SharedConsole",
            StillRunningReason.Cancelled => "Still_Cancelled",
            _ => "Still_DidNotExit",
        }),
        _ => Loc.Get("Outcome_" + outcome),
    };
}

public enum OperationKind
{
    Graceful,
    Force,
}

public sealed record OperationReport
{
    public required Guid OperationId { get; init; }

    public required OperationKind Kind { get; init; }

    public required DateTime StartedAtUtc { get; init; }

    public required DateTime FinishedAtUtc { get; init; }

    public required IReadOnlyList<ItemResult> Items { get; init; }

    public required MemoryStatus MemoryBefore { get; init; }

    public required MemoryStatus MemoryAfter { get; init; }

    public int ClosedCount => Items.Count(i => i.Outcome is ItemOutcome.Closed or ItemOutcome.PartiallyClosed or ItemOutcome.Terminated);

    public int StillRunningCount => Items.Count(i => i.CanForce);

    public int SkippedCount => Items.Count(i => i.Outcome is ItemOutcome.Skipped or ItemOutcome.Failed && !i.CanForce);

    public long MemoryFreedBytes => Items.Sum(i => i.MemoryFreedBytes);

    public long AvailableMemoryGainBytes => MemoryAfter.AvailablePhysicalBytes - MemoryBefore.AvailablePhysicalBytes;

    public string Summary =>
        Loc.Format("Report_Summary", ClosedCount, Items.Count, ByteSize.Format(MemoryFreedBytes))
        + (StillRunningCount > 0 ? Loc.Format("Report_StillRunningSuffix", StillRunningCount) : "");
}

/// <summary>Result of a dry run: what would happen, as judged by the live safety checks.</summary>
public sealed record DryRunStep(PlannedStep Step, GuardDecision Decision);

public sealed record DryRunItem(PlanItem Item, GuardDecision ItemDecision, IReadOnlyList<DryRunStep> Steps);

public sealed record DryRunReport(DateTime TakenAtUtc, IReadOnlyList<DryRunItem> Items)
{
    public int WouldActOn => Items.Count(i => i.ItemDecision.Allowed && i.Steps.Any(s => s.Decision.Allowed));
}
