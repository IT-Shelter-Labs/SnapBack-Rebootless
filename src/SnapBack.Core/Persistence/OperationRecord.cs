using SnapBack.Core.Execution;

namespace SnapBack.Core.Persistence;

/// <summary>History entry of one operation. Contains no window titles or command lines.</summary>
public sealed record OperationRecord
{
    public Guid OperationId { get; init; }

    public OperationKind Kind { get; init; }

    public DateTime StartedAtUtc { get; init; }

    public DateTime FinishedAtUtc { get; init; }

    public int Requested { get; init; }

    public int Closed { get; init; }

    public int StillRunning { get; init; }

    public long MemoryFreedBytes { get; init; }

    public long AvailableBeforeBytes { get; init; }

    public long AvailableAfterBytes { get; init; }

    public List<OperationRecordItem> Items { get; init; } = [];

    public static OperationRecord From(OperationReport report) => new()
    {
        OperationId = report.OperationId,
        Kind = report.Kind,
        StartedAtUtc = report.StartedAtUtc,
        FinishedAtUtc = report.FinishedAtUtc,
        Requested = report.Items.Count,
        Closed = report.ClosedCount,
        StillRunning = report.StillRunningCount,
        MemoryFreedBytes = report.MemoryFreedBytes,
        AvailableBeforeBytes = report.MemoryBefore.AvailablePhysicalBytes,
        AvailableAfterBytes = report.MemoryAfter.AvailablePhysicalBytes,
        Items = report.Items.Select(i => new OperationRecordItem
        {
            DisplayName = i.Item.Group.DisplayName,
            AppKey = i.Item.Group.AppKey,
            Outcome = i.Outcome,
            Message = i.Message,
            ProcessesExited = i.ProcessesExited,
            ProcessesRemaining = i.ProcessesRemaining,
            MemoryFreedBytes = i.MemoryFreedBytes,
        }).ToList(),
    };
}

public sealed record OperationRecordItem
{
    public string DisplayName { get; init; } = "";

    public string AppKey { get; init; } = "";

    public ItemOutcome Outcome { get; init; }

    public string Message { get; init; } = "";

    public int ProcessesExited { get; init; }

    public int ProcessesRemaining { get; init; }

    public long MemoryFreedBytes { get; init; }
}
