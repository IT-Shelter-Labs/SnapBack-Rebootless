namespace SnapBack.Core.Model;

/// <summary>Physical memory status of the machine.</summary>
/// <remarks>
/// Windows counts the standby list (cached file data and recently freed pages) as <em>available</em>:
/// it is handed to applications instantly when they need memory. "In use" therefore means memory
/// that applications and the system actually occupy. Detailed list sizes are optional because some
/// sources may be unavailable; unknown values are <c>null</c>.
/// </remarks>
public sealed record MemoryStatus(long TotalPhysicalBytes, long AvailablePhysicalBytes)
{
    public static readonly MemoryStatus Unknown = new(0, 0);

    /// <summary>Standby list: cached data that can be reused immediately (counts as available).</summary>
    public long? StandbyBytes { get; init; }

    /// <summary>Modified list: cached data that must be written to disk before the memory can be reused.</summary>
    public long? ModifiedBytes { get; init; }

    /// <summary>Free and zeroed pages.</summary>
    public long? FreeBytes { get; init; }

    /// <summary>System cache as reported by GetPerformanceInfo (fallback when list sizes are unknown).</summary>
    public long? SystemCacheBytes { get; init; }

    public long CommitTotalBytes { get; init; }

    public long CommitLimitBytes { get; init; }

    public bool IsKnown => TotalPhysicalBytes > 0;

    public long UsedPhysicalBytes => Math.Max(0, TotalPhysicalBytes - AvailablePhysicalBytes);

    public double UsedPercent => TotalPhysicalBytes <= 0 ? 0 : 100.0 * UsedPhysicalBytes / TotalPhysicalBytes;

    public double AvailablePercent => TotalPhysicalBytes <= 0 ? 0 : 100.0 * AvailablePhysicalBytes / TotalPhysicalBytes;

    /// <summary>"Cached" the way Task Manager shows it: standby + modified (or the system cache as fallback).</summary>
    public long? CachedBytes => StandbyBytes is { } standby ? standby + (ModifiedBytes ?? 0) : SystemCacheBytes;
}
