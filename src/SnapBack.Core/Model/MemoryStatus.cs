namespace SnapBack.Core.Model;

/// <summary>Physical memory status of the machine.</summary>
public sealed record MemoryStatus(long TotalPhysicalBytes, long AvailablePhysicalBytes)
{
    public static readonly MemoryStatus Unknown = new(0, 0);

    public long UsedPhysicalBytes => Math.Max(0, TotalPhysicalBytes - AvailablePhysicalBytes);

    public double UsedPercent => TotalPhysicalBytes <= 0 ? 0 : 100.0 * UsedPhysicalBytes / TotalPhysicalBytes;
}
