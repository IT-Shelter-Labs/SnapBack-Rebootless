using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Planning;

namespace SnapBack.Core.Insights;

public enum ImpactLevel
{
    Unknown,

    /// <summary>Nothing would be closed.</summary>
    None,
    Low,
    Medium,
    High,
}

/// <summary>
/// Where the memory in use goes. "In use" excludes the cache (Windows counts cached memory as
/// available), so the cache is reported next to it, never inside it.
/// </summary>
public sealed record MemoryBreakdown
{
    public long TotalBytes { get; init; }

    /// <summary>Installed memory minus available memory.</summary>
    public long InUseBytes { get; init; }

    /// <summary>Private memory of applications that are not part of the profile.</summary>
    public long NewAppsBytes { get; init; }

    /// <summary>Private memory of applications that belong to the profile or are always kept.</summary>
    public long ProfileAppsBytes { get; init; }

    /// <summary>The rest of "in use": Windows, drivers, services, protected components, shared memory.</summary>
    public long SystemAndOtherBytes { get; init; }

    /// <summary>Standby + modified cache. Already available; not part of <see cref="InUseBytes"/>.</summary>
    public long? CacheBytes { get; init; }
}

/// <summary>
/// A rough, honest estimate of what returning to the profile would achieve, made before anything
/// is closed.
/// </summary>
/// <remarks>
/// The estimate is the private memory of the applications that would be closed. It is not a
/// promise: memory shared with other processes stays in use, apps may refuse to close, and Windows
/// may keep part of the released memory as cache (which is still available). The level compares
/// the estimate with installed memory: 10 % or more is high, 4 % or more (or eight apps and more)
/// medium, anything else low.
/// </remarks>
public sealed record ImpactEstimate
{
    public const double HighShare = 0.10;
    public const double MediumShare = 0.04;
    public const int MediumAppCount = 8;

    public ImpactLevel Level { get; init; }

    public long EstimatedReclaimBytes { get; init; }

    public int AppsToClose { get; init; }

    public required MemoryBreakdown Breakdown { get; init; }

    public MemoryPressure Pressure { get; init; }

    /// <summary>The new applications account for less than a quarter of the memory in use.</summary>
    public bool MostMemoryIsNotFromNewApps { get; init; }

    /// <summary>Plain-language explanations, most important first (in the current UI language).</summary>
    public IReadOnlyList<string> Explanations
    {
        get
        {
            var list = new List<string>();
            if (Level == ImpactLevel.None)
            {
                list.Add(Loc.Get("Impact_Explain_NothingSelected"));
            }

            if (MostMemoryIsNotFromNewApps && Level is ImpactLevel.Low or ImpactLevel.Medium or ImpactLevel.None)
            {
                list.Add(Loc.Get("Impact_Explain_MostlyNotNew"));
            }

            if (Pressure == MemoryPressure.Healthy && Level != ImpactLevel.High)
            {
                list.Add(Loc.Get("Impact_Explain_PlentyAvailable"));
            }

            if (Pressure is MemoryPressure.Low or MemoryPressure.Moderate && Level == ImpactLevel.High)
            {
                list.Add(Loc.Get("Impact_Explain_ShouldHelp"));
            }

            list.Add(Loc.Get("Impact_Explain_Estimate"));
            return list;
        }
    }

    public static ImpactEstimate Compute(Analysis analysis, IEnumerable<PlanItem> toClose)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(toClose);

        var memory = analysis.Memory;
        var plan = analysis.Plan;
        var newApps = plan?.Candidates.Sum(c => c.Group.ActionablePrivateWorkingSet) ?? 0;
        var profileApps = plan?.Kept.Sum(k => k.Group.PrivateWorkingSet) ?? 0;
        var inUse = memory.UsedPhysicalBytes;
        var breakdown = new MemoryBreakdown
        {
            TotalBytes = memory.TotalPhysicalBytes,
            InUseBytes = inUse,
            NewAppsBytes = newApps,
            ProfileAppsBytes = profileApps,
            SystemAndOtherBytes = Math.Max(0, inUse - newApps - profileApps),
            CacheBytes = memory.CachedBytes,
        };

        var items = toClose.Where(i => i.IsSelectable).DistinctBy(i => i.Group.Id).ToList();
        var reclaim = items.Sum(i => i.Group.ActionablePrivateWorkingSet);
        var level = plan is null || !memory.IsKnown
            ? ImpactLevel.Unknown
            : LevelFor(reclaim, items.Count, memory.TotalPhysicalBytes);

        return new ImpactEstimate
        {
            Level = level,
            EstimatedReclaimBytes = reclaim,
            AppsToClose = items.Count,
            Breakdown = breakdown,
            Pressure = MemoryAdvisor.Assess(memory),
            MostMemoryIsNotFromNewApps = inUse > 0 && newApps < inUse / 4,
        };
    }

    public static ImpactLevel LevelFor(long reclaimBytes, int apps, long totalBytes)
    {
        if (apps == 0)
        {
            return ImpactLevel.None;
        }

        var share = totalBytes > 0 ? (double)reclaimBytes / totalBytes : 0;
        if (share >= HighShare)
        {
            return ImpactLevel.High;
        }

        return share >= MediumShare || apps >= MediumAppCount ? ImpactLevel.Medium : ImpactLevel.Low;
    }
}
