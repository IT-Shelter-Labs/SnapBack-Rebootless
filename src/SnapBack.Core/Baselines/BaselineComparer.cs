using SnapBack.Core.Grouping;
using SnapBack.Core.Model;

namespace SnapBack.Core.Baselines;

/// <summary>How a running application instance relates to the baseline.</summary>
public enum GroupStatus
{
    /// <summary>This exact instance (same root process) was running at baseline time.</summary>
    InBaseline,

    /// <summary>The app was in the baseline; the original instance is gone and this one replaced it.</summary>
    RestartedSinceBaseline,

    /// <summary>The app was in the baseline, but this is an additional instance.</summary>
    NewInstanceOfBaselineApp,

    /// <summary>The app was not running at baseline time.</summary>
    New,
}

public sealed record GroupComparison(GroupStatus Status, string? StartedByBaselineApp)
{
    public bool IsPartOfBaseline => Status is GroupStatus.InBaseline or GroupStatus.RestartedSinceBaseline;
}

public sealed class ComparisonResult
{
    public required Baseline Baseline { get; init; }

    /// <summary>Whether the baseline was taken during the current boot (so process identities are comparable).</summary>
    public required bool IsSameBoot { get; init; }

    public required IReadOnlyDictionary<ProcessIdentity, GroupComparison> ByGroup { get; init; }

    /// <summary>Baseline apps that are no longer running.</summary>
    public required IReadOnlyList<BaselineApp> ClosedSinceBaseline { get; init; }

    public GroupComparison For(AppGroup group) =>
        ByGroup.TryGetValue(group.Id, out var c) ? c : new GroupComparison(GroupStatus.New, null);
}

public static class BaselineComparer
{
    /// <summary>Tolerance when comparing boot times (clock adjustments, sleep).</summary>
    public static readonly TimeSpan BootTimeTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Compares the current applications with the baseline.
    /// </summary>
    /// <remarks>
    /// Matching works on two levels. Within the same boot, an instance whose root process identity
    /// (PID + creation time) is in the baseline is exactly the same instance. Beyond that, apps are
    /// matched by <see cref="AppGroup.AppKey"/>: if a baseline instance has disappeared, a newer
    /// instance of the same app takes its place (the app was restarted or updated). Only instances
    /// beyond the baseline count are reported as new.
    /// </remarks>
    public static ComparisonResult Compare(Baseline baseline, IReadOnlyList<AppGroup> groups, SessionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(groups);

        var baselineRoots = baseline.Apps.SelectMany(a => a.Instances).ToHashSet();
        var currentRoots = groups.Select(g => g.Id).ToHashSet();

        var isSameBoot = (baseline.BootTimeUtc - ctx.BootTimeUtc).Duration() <= BootTimeTolerance
            || currentRoots.Overlaps(baselineRoots);

        var status = new Dictionary<ProcessIdentity, GroupStatus>();
        if (isSameBoot)
        {
            foreach (var g in groups.Where(g => baselineRoots.Contains(g.Id)))
            {
                status[g.Id] = GroupStatus.InBaseline;
            }
        }

        var instancesByKey = baseline.Apps
            .GroupBy(a => a.AppKey, StringComparer.Ordinal)
            .ToDictionary(k => k.Key, k => k.SelectMany(a => a.Instances).ToList(), StringComparer.Ordinal);

        foreach (var keyGroup in groups.Where(g => !status.ContainsKey(g.Id)).GroupBy(g => g.AppKey, StringComparer.Ordinal))
        {
            if (!instancesByKey.TryGetValue(keyGroup.Key, out var baselineInstances))
            {
                foreach (var g in keyGroup)
                {
                    status[g.Id] = GroupStatus.New;
                }

                continue;
            }

            var stillRunning = isSameBoot ? baselineInstances.Count(currentRoots.Contains) : 0;
            var vacancies = baselineInstances.Count - stillRunning;
            foreach (var g in keyGroup.OrderBy(g => g.Root.Identity.CreationTime))
            {
                status[g.Id] = vacancies-- > 0 ? GroupStatus.RestartedSinceBaseline : GroupStatus.NewInstanceOfBaselineApp;
            }
        }

        var byId = groups.ToDictionary(g => g.Id);
        var result = new Dictionary<ProcessIdentity, GroupComparison>();
        foreach (var g in groups)
        {
            var s = status[g.Id];
            string? startedBy = null;
            if (s is GroupStatus.New or GroupStatus.NewInstanceOfBaselineApp
                && g.LaunchedBy is { } parentId
                && byId.TryGetValue(parentId, out var parent)
                && !parent.IsProtected
                && status.TryGetValue(parentId, out var parentStatus)
                && parentStatus is GroupStatus.InBaseline or GroupStatus.RestartedSinceBaseline)
            {
                startedBy = parent.DisplayName;
            }

            result[g.Id] = new GroupComparison(s, startedBy);
        }

        var runningKeys = groups.Select(g => g.AppKey).ToHashSet(StringComparer.Ordinal);
        var closed = baseline.Apps.Where(a => !runningKeys.Contains(a.AppKey) && !a.WasProtected).ToList();

        return new ComparisonResult
        {
            Baseline = baseline,
            IsSameBoot = isSameBoot,
            ByGroup = result,
            ClosedSinceBaseline = closed,
        };
    }
}
