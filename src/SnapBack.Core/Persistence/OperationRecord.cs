using SnapBack.Core.Execution;
using SnapBack.Core.Memory;

namespace SnapBack.Core.Persistence;

/// <summary>
/// History entry of one "Return to profile" operation, including a later forced termination or
/// Deep Memory Cleanup of the same operation. Contains no window titles or command lines.
/// </summary>
/// <remarks>Values added in version 0.3 are nullable: older entries simply do not have them.</remarks>
public sealed record OperationRecord
{
    public Guid OperationId { get; init; }

    public OperationKind Kind { get; init; }

    public DateTime StartedAtUtc { get; init; }

    public DateTime FinishedAtUtc { get; init; }

    public Guid? ProfileId { get; init; }

    public string? ProfileName { get; init; }

    public int Requested { get; init; }

    public int Closed { get; init; }

    public int StillRunning { get; init; }

    /// <summary>Processes ended by forced termination (after the user's explicit confirmation).</summary>
    public int ForceTerminations { get; init; }

    /// <summary>Private memory released by the applications that closed.</summary>
    public long MemoryFreedBytes { get; init; }

    public long AvailableBeforeBytes { get; init; }

    public long AvailableAfterBytes { get; init; }

    public int? AppsBefore { get; init; }

    public int? AppsAfter { get; init; }

    public int? ProcessesBefore { get; init; }

    public int? ProcessesAfter { get; init; }

    public long? InUseBeforeBytes { get; init; }

    public long? InUseAfterBytes { get; init; }

    public bool DeepCleanupUsed { get; init; }

    /// <summary>Cache dropped by the Deep Memory Cleanup; kept apart from <see cref="MemoryFreedBytes"/>.</summary>
    public long? DeepCleanupReclaimedBytes { get; init; }

    /// <summary>Managed environments stopped as part of the operation ("WSL", "Docker").</summary>
    public List<string> EnvironmentsStopped { get; init; } = [];

    public List<OperationRecordItem> Items { get; init; } = [];

    public TimeSpan Duration => FinishedAtUtc > StartedAtUtc ? FinishedAtUtc - StartedAtUtc : TimeSpan.Zero;

    public static OperationRecord From(OperationReport report) => Merge(null, report);

    /// <summary>
    /// Adds the result of a (graceful or forced) run to the record of the same operation. Items of
    /// the run replace the earlier (cumulative) result of the same application.
    /// </summary>
    public static OperationRecord Merge(OperationRecord? existing, OperationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var items = (existing?.Items ?? []).ToList();
        foreach (var i in report.Items)
        {
            var item = new OperationRecordItem
            {
                DisplayName = i.Item.Group.DisplayName,
                AppKey = i.Item.Group.AppKey,
                RootPid = i.Item.Group.Id.Pid,
                RootCreationTime = i.Item.Group.Id.CreationTime,
                Outcome = i.Outcome,
                Message = i.Message,
                ProcessesExited = i.ProcessesExited,
                ProcessesRemaining = i.ProcessesRemaining,
                ProcessesTerminated = i.ProcessesTerminated,
                MemoryFreedBytes = i.MemoryFreedBytes,
            };

            // One entry per app instance (root process identity), so two instances of the same app stay apart.
            var index = items.FindIndex(x => x.SameInstance(item));
            if (index >= 0)
            {
                // Results are cumulative per application: a forced run reports everything that
                // exited so far, so it simply replaces the earlier result.
                items[index] = item;
            }
            else
            {
                items.Add(item);
            }
        }

        var record = existing ?? new OperationRecord
        {
            OperationId = report.OperationId,
            Kind = report.Kind,
            StartedAtUtc = report.StartedAtUtc,
            AvailableBeforeBytes = report.MemoryBefore.AvailablePhysicalBytes,
        };

        return record with
        {
            FinishedAtUtc = report.FinishedAtUtc > record.FinishedAtUtc ? report.FinishedAtUtc : record.FinishedAtUtc,
            Requested = items.Count,
            Closed = items.Count(i => i.Outcome is ItemOutcome.Closed or ItemOutcome.PartiallyClosed or ItemOutcome.Terminated),
            StillRunning = items.Count(i => i.Outcome is ItemOutcome.StillRunning or ItemOutcome.PartiallyClosed or ItemOutcome.Failed
                && i.ProcessesRemaining > 0),
            ForceTerminations = items.Sum(i => i.ProcessesTerminated),
            MemoryFreedBytes = items.Sum(i => i.MemoryFreedBytes),
            AvailableAfterBytes = report.MemoryAfter.AvailablePhysicalBytes,
            Items = items,
        };
    }

    /// <summary>Adds the measured before/after statistics.</summary>
    public OperationRecord WithStatistics(SessionStatistics before, SessionStatistics after) => this with
    {
        AppsBefore = before.Apps,
        AppsAfter = after.Apps,
        ProcessesBefore = before.Processes,
        ProcessesAfter = after.Processes,
        InUseBeforeBytes = before.Memory.IsKnown ? before.Memory.UsedPhysicalBytes : null,
        InUseAfterBytes = after.Memory.IsKnown ? after.Memory.UsedPhysicalBytes : null,
        AvailableBeforeBytes = before.Memory.IsKnown ? before.Memory.AvailablePhysicalBytes : AvailableBeforeBytes,
        AvailableAfterBytes = after.Memory.IsKnown ? after.Memory.AvailablePhysicalBytes : AvailableAfterBytes,
    };

    public OperationRecord WithDeepCleanup(DeepCleanupReport report) => this with
    {
        DeepCleanupUsed = DeepCleanupUsed || report.Outcome.Succeeded,
        DeepCleanupReclaimedBytes = report.Outcome.Succeeded
            ? (DeepCleanupReclaimedBytes ?? 0) + report.CacheReclaimedBytes
            : DeepCleanupReclaimedBytes,
    };

    public OperationRecord WithEnvironmentStopped(string name) =>
        EnvironmentsStopped.Contains(name) ? this : this with { EnvironmentsStopped = [.. EnvironmentsStopped, name] };
}

public sealed record OperationRecordItem
{
    public string DisplayName { get; init; } = "";

    public string AppKey { get; init; } = "";

    /// <summary>Root process of the app instance (identifies the instance within one operation).</summary>
    public int RootPid { get; init; }

    public long RootCreationTime { get; init; }

    public ItemOutcome Outcome { get; init; }

    public string Message { get; init; } = "";

    public int ProcessesExited { get; init; }

    public int ProcessesRemaining { get; init; }

    public int ProcessesTerminated { get; init; }

    public long MemoryFreedBytes { get; init; }

    public bool SameInstance(OperationRecordItem other) =>
        RootPid == other.RootPid && RootCreationTime == other.RootCreationTime && AppKey == other.AppKey;
}
