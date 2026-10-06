using SnapBack.Core.Model;

namespace SnapBack.Core.Memory;

/// <summary>Counts and memory of the user session at one moment (for before/after comparisons).</summary>
public sealed record SessionStatistics(int Apps, int Processes, MemoryStatus Memory, DateTime TakenAtUtc)
{
    public static SessionStatistics From(Analysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return new SessionStatistics(analysis.RunningAppCount, analysis.Snapshot.Processes.Count, analysis.Memory, analysis.Snapshot.TakenAtUtc);
    }
}

public enum ComparisonValueKind
{
    Count,
    Bytes,
}

/// <summary>One "Before → After" line.</summary>
public sealed record ComparisonRow(string LabelKey, ComparisonValueKind Kind, long? Before, long? After)
{
    public long? Delta => Before is { } b && After is { } a ? a - b : null;
}

/// <summary>
/// Result of a "Return to baseline" in numbers. Memory released by closing apps and memory reclaimed
/// by an optional Deep Memory Cleanup are kept strictly apart: they are different things.
/// </summary>
public sealed record RestoreComparison(SessionStatistics Before, SessionStatistics After, long ReleasedByAppsBytes)
{
    /// <summary>The Deep Memory Cleanup run after the restore, if the user chose to run one.</summary>
    public DeepCleanupReport? DeepCleanup { get; init; }

    public IReadOnlyList<ComparisonRow> Rows =>
    [
        new("Cmp_Apps", ComparisonValueKind.Count, Before.Apps, After.Apps),
        new("Cmp_Processes", ComparisonValueKind.Count, Before.Processes, After.Processes),
        new("Cmp_InUse", ComparisonValueKind.Bytes, Known(Before.Memory, m => m.UsedPhysicalBytes), Known(After.Memory, m => m.UsedPhysicalBytes)),
        new("Cmp_Available", ComparisonValueKind.Bytes, Known(Before.Memory, m => m.AvailablePhysicalBytes), Known(After.Memory, m => m.AvailablePhysicalBytes)),
        new("Cmp_Cached", ComparisonValueKind.Bytes, Before.Memory.CachedBytes, After.Memory.CachedBytes),
    ];

    private static long? Known(MemoryStatus m, Func<MemoryStatus, long> value) => m.IsKnown ? value(m) : null;
}
